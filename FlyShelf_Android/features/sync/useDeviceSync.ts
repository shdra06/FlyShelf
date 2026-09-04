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
import { DirectMesh } from '../../utils/directMesh';
import AsyncStorage from '@react-native-async-storage/async-storage';

const { AdvanceOverlay } = NativeModules;

// Audit Task 1: normalizeTextForFingerprint now imported from utils/textNormalize.ts

let recentNotificationCount = 0;
let recentNotificationTimer: ReturnType<typeof setTimeout> | null = null;

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
  sentContentFingerprintsRef: React.MutableRefObject<Map<string, number>>;
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
    const enqueueDownloadRef = useRef(enqueueDownload);
  useEffect(() => { enqueueDownloadRef.current = enqueueDownload; }, [enqueueDownload]);
  const markPcReachableRef = useRef(markPcReachable);
  useEffect(() => { markPcReachableRef.current = markPcReachable; }, [markPcReachable]);
  const markPcUnreachableRef = useRef(markPcUnreachable);
  useEffect(() => { markPcUnreachableRef.current = markPcUnreachable; }, [markPcUnreachable]);
  const updateDeviceStatusRef = useRef(updateDeviceStatus);
  useEffect(() => { updateDeviceStatusRef.current = updateDeviceStatus; }, [updateDeviceStatus]);
  const setConnectionInfoRef = useRef(setConnectionInfo);
  useEffect(() => { setConnectionInfoRef.current = setConnectionInfo; }, [setConnectionInfo]);
  const setClipsRef = useRef(setClips);
  useEffect(() => { setClipsRef.current = setClips; }, [setClips]);
  const setLastCopiedTextRef = useRef(setLastCopiedText);
  useEffect(() => { setLastCopiedTextRef.current = setLastCopiedText; }, [setLastCopiedText]);
  const setImageDownloadTriggerRef = useRef(setImageDownloadTrigger);
  useEffect(() => { setImageDownloadTriggerRef.current = setImageDownloadTrigger; }, [setImageDownloadTrigger]);
  const setRichMediaDownloadTriggerRef = useRef(setRichMediaDownloadTrigger);
  useEffect(() => { setRichMediaDownloadTriggerRef.current = setRichMediaDownloadTrigger; }, [setRichMediaDownloadTrigger]);
  const scrollToTopRef = useRef(scrollToTop);
  useEffect(() => { scrollToTopRef.current = scrollToTop; }, [scrollToTop]);
  const recordCloudflareFailureRef = useRef(recordCloudflareFailure);
  useEffect(() => { recordCloudflareFailureRef.current = recordCloudflareFailure; }, [recordCloudflareFailure]);
  const resetCloudflareFailCountRef = useRef(resetCloudflareFailCount);
  useEffect(() => { resetCloudflareFailCountRef.current = resetCloudflareFailCount; }, [resetCloudflareFailCount]);
  const invalidatePcUrlCacheRef = useRef(invalidatePcUrlCache);
  useEffect(() => { invalidatePcUrlCacheRef.current = invalidatePcUrlCache; }, [invalidatePcUrlCache]);

  const pollLockRef = useRef(false); // Prevents concurrent pollFn from timer + long-poll
  const pollRetryCountRef = useRef(0); // Exponential backoff counter for failed polls
  const shortcutSyncTimestampRef = useRef<number>(0); // Throttle: sync shortcuts every 60s
  const lastSyncTimestampRef = useRef<number>(0); // Incremental delta sync timestamp
  const isFirstSyncRef = useRef<boolean>(true); // Initial pairing sync tracker

  // Restore last sync timestamp from storage to prevent re-flooding on app restart
  useEffect(() => {
    AsyncStorage.getItem('flyshelf_lastSyncTimestamp').then(v => {
      const ts = parseInt(v || '0', 10);
      if (ts > 0) {
        lastSyncTimestampRef.current = ts;
        isFirstSyncRef.current = false; // Already paired before — skip initial flood
        syncLog('PC-POLL', `Restored lastSyncTimestamp from storage: ${ts}`);
      }
    }).catch(() => {});
  }, []);

  useEffect(() => {
    const pollFn = async () => {
      if (isTornDown) return; // Prevent execution after unmount
      if (pollLockRef.current) return; // Already running — skip this invocation
      // BUG FIX #1: Skip poll if pairing key hasn't loaded from SecureStorage yet.
      // Without this, the first poll fires with empty X-Pairing-Key → PC rejects → URL cache destroyed.
      if (!pairingKeyRef.current) return;
      pollLockRef.current = true;
      // Prune entries older than 1 hour
      const ONE_HOUR = 3600000;
      const nowTs = Date.now();
      for (const [key, timestamp] of sentContentFingerprintsRef.current.entries()) {
        if (nowTs - timestamp > ONE_HOUR) sentContentFingerprintsRef.current.delete(key);
      }

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
        try { if (typeof AdvanceOverlay.setPcUrl === 'function') AdvanceOverlay.setPcUrl(targetUrl); } catch(e) {}
        try { if (deviceName && typeof AdvanceOverlay.setDeviceName === 'function') AdvanceOverlay.setDeviceName(deviceName); } catch(e) {}
      }
      try {
        const timeout = targetUrl.includes('trycloudflare.com') ? 5000 : 2000;
        const syncHeaders: Record<string, string> = { 'X-FlyShelf-Client': 'MobileCompanion', 'Connection': 'keep-alive' };
        if (pairingKeyRef.current) syncHeaders['X-Pairing-Key'] = pairingKeyRef.current;
        // Send device identity so PC can update device status and show real name
        if (deviceName) {
          syncHeaders['X-Source-Device'] = deviceName;
          syncHeaders['X-Device-Id'] = `Mobile_${deviceName.replace(/[^a-zA-Z0-9_]/g, '_')}`;
        }
        const pollStart = performance.now();
        const syncUrl = lastSyncTimestampRef.current > 0
          ? `${targetUrl}/api/sync?since=${lastSyncTimestampRef.current}`
          : `${targetUrl}/api/sync?limit=5`;
        // DEV DEBUG: Log every poll attempt
        syncLog('PC-POLL', `[STEP 5/6: SYNC POLL 1/3] → ${syncUrl.replace(targetUrl, '')} (Target: ${targetUrl}, Timeout: ${timeout}ms, Retries: ${pollRetryCountRef.current})`);
        const response = await fetchWithTimeout(syncUrl, { headers: syncHeaders }, timeout);
        if (!response.ok) {
          // DEV DEBUG: Log failure details
          syncLog('PC-POLL', `[STEP 5/6: SYNC POLL 1/3 ERROR] ❌ HTTP ${response.status} ${response.statusText} | url=${targetUrl}`);
          pollRetryCountRef.current++;
          // Require 5+ consecutive failures before declaring offline
          // This prevents momentary Cloudflare 502s or network blips from causing flicker
          if (pollRetryCountRef.current >= 5) {
            // DON'T wipe cachedPcUrlRef — keep using the same URL for retries
            // DON'T wipe stored URLs from SecureStore — they're the fallback lifeline
            // Only re-query Firebase for a NEW URL if Cloudflare fails consistently
            if (targetUrl.includes('trycloudflare.com')) {
              if (recordCloudflareFailureRef.current()) {
                try {
                  const { decryptDeviceList } = require('../../utils/networkHelpers');
                  const snapshot = await get(ref(database, `active_devices/${pairingKeyRef.current}`));
                  if (snapshot.exists()) {
                    const data = snapshot.val();
                    const filtered = Object.keys(data).map(k => ({ ...data[k], _key: k })).filter(d => d.IsOnline !== false);
                    const rawDevices = await decryptDeviceList(filtered, pairingKeyRef.current);
                    const pcDev = rawDevices.find((d: any) => d.DeviceType === 'PC');
                    if (pcDev && pcDev.GlobalUrl && pcDev.GlobalUrl.includes('trycloudflare.com')) {
                      const newUrl = pcDev.GlobalUrl.trim().replace(/\/$/, '');
                      if (newUrl !== targetUrl) {
                        // Found a genuinely NEW URL — update everything
                        setSecureItem('lastCloudflareUrl', newUrl).catch(() => {});
                        setSecureItem('pairedGlobalUrl', newUrl).catch(() => {});
                        AsyncStorage.setItem('lastCloudflareUrl', newUrl).catch(() => {});
                        AsyncStorage.setItem('pairedGlobalUrl', newUrl).catch(() => {});
                        cachedPcUrlRef.current = newUrl;
                        pollRetryCountRef.current = 0; // Reset — try new URL immediately
                        syncLog('PC-POLL', `Firebase re-read found NEW Cloudflare URL: ${newUrl}`);
                      }
                      // If same URL, do NOT delete it — the tunnel may be temporarily down
                    }
                  }
                } catch (e) {
                  syncLog('PC-POLL', `Firebase re-query failed: ${(e as any)?.message || e}`);
                  // Do NOT delete stored URLs on error
                }
              }
            }
            markPcUnreachableRef.current();
            // Keep connectionInfo showing last known type but mark as reconnecting
            // (don't set to null — that causes the "PC Offline" banner to flash)
          }
          return;
        }
        {
          // Phase 1: PC is reachable — disconnect Firebase listener if active
          markPcReachableRef.current();
          resetCloudflareFailCountRef.current();
          pollRetryCountRef.current = 0; // Reset backoff on successful connection
          // H-3: Connection quality indicator
          const pollLatency = Math.round(performance.now() - pollStart);
          // DEV DEBUG: Log every successful poll
          syncLog('PC-POLL', `✓ OK: ${response.status} | ${targetUrl.includes('trycloudflare.com') ? 'Cloud' : 'LAN'} | latency=${pollLatency}ms | url=${targetUrl.substring(0, 50)}`);
          setConnectionInfoRef.current({ url: targetUrl, latencyMs: pollLatency, type: targetUrl.includes('trycloudflare.com') ? 'Cloud' : 'LAN' });
          // Calibrate clock against PC server timestamp
          const pcServerTimeStr = response.headers.get('X-Server-Time');
          if (pcServerTimeStr) {
            const pcServerTime = parseInt(pcServerTimeStr, 10);
            if (!isNaN(pcServerTime)) {
              NetworkClock.calibratePeer(pcServerTime, pollLatency);
            }
          }
          // Read PC identity from response headers — PC sends name, ID, and transport status
          const pcDeviceName = response.headers.get('X-PC-DeviceName') || '';
          const pcDeviceId = response.headers.get('X-PC-DeviceId') || '';
          // Update paired device status with real PC name, connection type & latency
          const connType = targetUrl.includes('trycloudflare.com') ? 'Cloud' as const : 'LAN' as const;
          pairedDevicesRef.current.filter(d => d.deviceType === 'PC').forEach(d => {
            updateDeviceStatusRef.current(pcDeviceId || d.deviceId, {
              isOnline: true, connectionType: connType, latencyMs: pollLatency, lastSeen: NetworkClock.now(),
              localUrl: connType === 'LAN' ? targetUrl : undefined,
              globalUrl: connType === 'Cloud' ? targetUrl : undefined,
            });
          });
          // Mark this URL as proven-working for the image sweep to use
          lastWorkingPcUrlRef.current = targetUrl;
          // Persist for background sync task access
          AsyncStorage.setItem('lastWorkingPcUrl', targetUrl).catch(() => {});
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
          // BUG FIX #4: Guard against captive portals (hotel WiFi) that return 200 OK with HTML.
          // Without this, response.json() throws on HTML, outer catch destroys URL cache, loops forever.
          const contentType = response.headers.get('content-type') || '';
          if (!contentType.includes('application/json')) {
            syncLog('PC-POLL', `Non-JSON response (${contentType.substring(0, 40)}) — likely captive portal, skipping`);
            pollLockRef.current = false;
            return;
          }
          let data: any;
          try {
            data = await response.json();
          } catch {
            syncLog('PC-POLL', 'Malformed JSON body — skipping');
            pollLockRef.current = false;
            return;
          }
          if (Array.isArray(data) && data.length > 0) {
            // Always advance lastSyncTimestamp to the NEWEST item in the FULL response
            // This ensures delta sync picks up from the true latest point even if we cap items below
            for (const item of data) {
              if (item.Timestamp && item.Timestamp > lastSyncTimestampRef.current) {
                lastSyncTimestampRef.current = item.Timestamp;
              }
            }
            // Persist timestamp so app restart doesn't re-flood
            AsyncStorage.setItem('flyshelf_lastSyncTimestamp', String(lastSyncTimestampRef.current)).catch(() => {});

            const isInitialPair = isFirstSyncRef.current;
            isFirstSyncRef.current = false;

            // CLIENT-SIDE CAP: Enforce max items even if PC server ignores the limit param
            // First pair: 5 newest items. Reconnect: 3 newest items. Delta sync: all new items.
            const maxItems = isInitialPair ? 5 : (lastSyncTimestampRef.current === 0 ? 3 : data.length);
            // Sort by timestamp descending so we always get the NEWEST items
            const sorted = [...data].sort((a, b) => (b.Timestamp || 0) - (a.Timestamp || 0));
            const capped = sorted.slice(0, maxItems);
            if (data.length > maxItems) {
              syncLog('PC-POLL', `Client-side cap: received ${data.length} items, processing ${maxItems} (${isInitialPair ? 'first pair' : 'reconnect'})`);
            }

            // Process capped items chronologically (oldest first → newest appears on top)
            const itemsToProcess = [...capped].reverse();

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
                      if (Platform.OS === 'android' && AdvanceOverlay && typeof AdvanceOverlay.setClipboardSuppressed === 'function') {
                        try { AdvanceOverlay.setClipboardSuppressed(latestRaw); } catch(e) { await Clipboard.setStringAsync(latestRaw); }
                      } else { await Clipboard.setStringAsync(latestRaw); }
                      setLastCopiedTextRef.current(latestRaw);
                      lastCopiedRef.current = latestRaw;
                      toast.clipboard(`Synced from ${latest.SourceDeviceName || 'PC'}`, latestRaw);
                    }

                    // Add text clip to feed so it is visible in the Android clip list
                    const resolvedTextItem: ClipItem = {
                      id: latest.id || `clip_${Date.now()}_${Math.random().toString(36).substring(2, 7)}`,
                      Raw: latestRaw,
                      Title: latest.Title || latestRaw,
                      Type: latest.Type,
                      Timestamp: latest.Timestamp || NetworkClock.now(),
                      Time: latest.Time || new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }),
                      SourceDeviceName: latest.SourceDeviceName || 'PC',
                      SourceDeviceType: latest.SourceDeviceType || 'PC',
                      _receivedVia: (cachedPcUrlRef.current?.includes('trycloudflare.com') ? 'Cloud' : 'LAN') as 'Cloud' | 'LAN',
                    };

                    setClipsRef.current(prev => {
                      const dupIdx = prev.findIndex(c =>
                        (c.id && resolvedTextItem.id && c.id === resolvedTextItem.id) ||
                        (c.Raw && resolvedTextItem.Raw && c.Raw === resolvedTextItem.Raw)
                      );
                      if (dupIdx >= 0) {
                        const updated = [...prev];
                        updated[dupIdx] = { ...updated[dupIdx], ...resolvedTextItem };
                        return updated;
                      }
                      scrollToTopRef.current();
                      return [resolvedTextItem, ...prev];
                    });
                  }
                } else if (latest.Type === 'Image' || latest.Type === 'ImageLink' || latest.Type === 'QRCode') {
                  // Add image to feed immediately with FULL LAN URLs resolved
                  // Use the currently-connected targetUrl for immediate download
                  const resolvedItem = { ...latest };
                  if (resolvedItem.PreviewUrl?.startsWith('/')) resolvedItem.PreviewUrl = `${targetUrl}${resolvedItem.PreviewUrl}`;
                  if (resolvedItem.DownloadUrl?.startsWith('/')) resolvedItem.DownloadUrl = `${targetUrl}${resolvedItem.DownloadUrl}`;
                  if (resolvedItem.Raw?.startsWith('/')) resolvedItem.Raw = `${targetUrl}${resolvedItem.Raw}`;
                  setClipsRef.current(prev => {
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
                    scrollToTopRef.current();
                    return [{ ...resolvedItem, _needsDownload: true, _receivedVia: (cachedPcUrlRef.current?.includes('trycloudflare.com') ? 'Cloud' : 'LAN') as 'Cloud' | 'LAN' } as any, ...prev];
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
                                  const d = await decryptDevice(devs[dk], pk);
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
                        enqueueDownloadRef.current({
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
                  setClipsRef.current(prev => {
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
                    scrollToTopRef.current();
                    return [{ ...latest, Raw: latest.Title || latest.Raw, Timestamp: NetworkClock.now(), _receivedVia: (cachedPcUrlRef.current?.includes('trycloudflare.com') ? 'Cloud' : 'LAN') as 'Cloud' | 'LAN' } as any, ...prev];
                  });
                  // REMOVED: UN-DELETE logic that brought back deleted items
                  // If user deleted an item locally, it stays deleted even if PC re-sends it
                }
                if (Platform.OS === 'android' && AdvanceOverlay && isFloatingBallEnabledRef.current && typeof AdvanceOverlay.pushClipToNativeDB === 'function') {
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
                  const isFile = ['Pdf', 'Document', 'File', 'Video', 'Audio', 'Archive', 'Presentation', 'Image', 'ImageLink', 'QRCode'].includes(latest.Type);
                  const channelId = isFile ? 'sync_files' : 'sync_clips';
                  const sourceDevice = latest.SourceDeviceName || 'PC';

                  recentNotificationCount++;
                  if (!recentNotificationTimer) {
                    recentNotificationTimer = setTimeout(() => {
                      recentNotificationCount = 0;
                      recentNotificationTimer = null;
                    }, 5000);
                  }

                  if (recentNotificationCount >= 3) {
                    Notifications.scheduleNotificationAsync({
                      identifier: 'sync_summary',
                      content: {
                        title: 'Sync Summary',
                        body: `📋 ${recentNotificationCount} new clips synced from ${sourceDevice}`,
                        categoryIdentifier: 'clip_action',
                      },
                      trigger: { channelId: channelId },
                    }).catch(() => {});
                  } else {
                    const rawBody = latest.Raw || latest.Title || '';
                    const body = rawBody.length > 100 ? rawBody.substring(0, 100) + '...' : rawBody;
                    
                    Notifications.scheduleNotificationAsync({
                      content: {
                        title: `📋 ${sourceDevice}`,
                        body: body,
                        categoryIdentifier: 'clip_action',
                      },
                      trigger: { channelId: channelId },
                    }).catch(() => {});
                  }
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
                          const d = await decryptDevice(devs[dk], pk);
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
                enqueueDownloadRef.current({
                  id: item.id || '', title: item.Title || safeName, type: item.Type,
                  fileUrl, destPath, source: dlSource,
                  sourceDevice: item.SourceDeviceName || 'PC',
                });
              }
            } catch (dlErr: any) {
              syncLog('PC-POLL', `File sweep error: ${item.Title} — ${dlErr?.message || dlErr}`);
            }
          }
          setClipsRef.current(current => {
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
                merged.unshift({ ...localItem, _receivedVia: localItem._receivedVia || 'Cloud' });
                changed = true;
                hasNewItems = true;
              }
            });
            if (!changed) return current;
            if (hasNewItems) scrollToTopRef.current(); // Only scroll for genuinely new items
            return merged;
          });
          // Trigger background download effects for new clips from LAN poll
          setImageDownloadTriggerRef.current(t => t + 1);
          setRichMediaDownloadTriggerRef.current(t => t + 1);
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
                  try {
                    if (typeof AdvanceOverlay?.syncShortcuts === 'function') {
                      AdvanceOverlay.syncShortcuts(JSON.stringify(scData));
                    }
                  } catch(e) {}
                  syncLog('SHORTCUTS', `Synced ${scData.length} shortcuts to overlay`);
                }
              }
            } catch (e) { /* Silent — shortcuts are non-critical */ }
          }
        }
      } catch (e) {
        syncLog('PC-POLL', `Poll failed: ${(e as any)?.message || e}`);
        pollRetryCountRef.current = Math.min(pollRetryCountRef.current + 1, 6); // Capped at 6 for fast recovery
        
        // Require 5+ consecutive failures before declaring offline
        // This prevents momentary timeouts from causing Cloud→Offline flickering
        if (pollRetryCountRef.current >= 5) {
          // DON'T wipe cachedPcUrlRef — keep using the same URL for retries
          recordCloudflareFailureRef.current();
          markPcUnreachableRef.current();
          // DON'T set connectionInfo to null — keep showing last known state
        }
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
      if (isTornDown) return; // Prevent orphaned timers after cleanup
      pollTimer = setTimeout(async () => {
        await pollFn();
        if (pollTimer !== null && !isTornDown) schedulePoll(); // Re-evaluate interval each cycle
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
    let wsReconnectDelay = 3000;
    const WS_MAX_RECONNECT_DELAY = 60000;

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
      if (!pairingKeyRef.current) { setTimeout(startWebSocketEngine, 1000); return; }
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
          wsReconnectDelay = 3000;
          if (isTornDown) { ws.close(); return; }
          wsConnected = true;
          longPollActive = false; // Disable fallback when WebSocket is live
          if (currentLongPollController) { currentLongPollController.abort(); currentLongPollController = null; }
          DirectMesh.registerWebSocket(ws);
          DirectMesh.drainOutbox();
          markPcReachableRef.current();
          resetCloudflareFailCountRef.current();
          pollRetryCountRef.current = 0;
          syncLog('WS-PEER', `✅ Persistent Duplex Socket ESTABLISHED to ${targetUrl} (${connType})`);

          // Send initial ping and start 15s heartbeat
          const sendPing = () => {
            if (ws.readyState === WebSocket.OPEN) {
              const pingPayload = JSON.stringify({ type: 'Ping', ts: Date.now() });
              ws.send(pingPayload);
            }
          };
          sendPing();
          clearInterval(wsPingInterval);
          wsPingInterval = setInterval(sendPing, 15000);
        };

        ws.onmessage = (event) => {
          try {
            const raw = typeof event.data === 'string' ? event.data : '';
            if (!raw) return;
            if (raw === 'pong') {
              markPcReachableRef.current();
              return;
            }

            if (raw.startsWith('{')) {
              const msg = JSON.parse(raw);
              if (msg.type === 'Pong') {
                const now = Date.now();
                const latency = msg.ts ? Math.max(1, Math.round(now - msg.ts)) : 5;
                setConnectionInfoRef.current({ url: targetUrl, latencyMs: latency, type: connType });
                pairedDevicesRef.current.filter(d => d.deviceType === 'PC').forEach(d => {
                  updateDeviceStatusRef.current(d.deviceId, { isOnline: true, connectionType: connType, latencyMs: latency, lastSeen: NetworkClock.now() });
                });
                markPcReachableRef.current();
              } else if (msg.type === 'UrlUpdate') {
                syncLog('WS-PEER', `⚡ Direct UrlUpdate from PC: LAN=${msg.lanUrl}, Cloud=${msg.cfUrl}`);
                if (msg.cfUrl) {
                  AsyncStorage.setItem('lastCloudflareUrl', msg.cfUrl).catch(() => {});
                  AsyncStorage.setItem('pairedGlobalUrl', msg.cfUrl).catch(() => {});
                  setSecureItem('pairedGlobalUrl', msg.cfUrl).catch(() => {});
                  setSecureItem('lastCloudflareUrl', msg.cfUrl).catch(() => {});
                  if (connType === 'Cloud') {
                    cachedPcUrlRef.current = msg.cfUrl;
                    cachedPcUrlTimestampRef.current = NetworkClock.now();
                  }
                }
                if (msg.lanUrl) {
                  AsyncStorage.setItem('@flyshelf_last_lan_url', msg.lanUrl).catch(() => {});
                  setSecureItem('pairedLocalUrl', msg.lanUrl).catch(() => {});
                  if (connType === 'LAN') {
                    cachedPcUrlRef.current = msg.lanUrl;
                    cachedPcUrlTimestampRef.current = NetworkClock.now();
                  }
                }
              } else if (msg.type !== 'Ping') {
                syncLog('WS-PEER', `⚡ Instant Push from PC via WebSocket (${msg.type || 'clip'})`);
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
          DirectMesh.registerWebSocket(null);
          if (isTornDown) return;
          syncLog('WS-PEER', `WebSocket closed (code=${ev.code}). Engaging fallback & reconnecting in ${wsReconnectDelay}ms.`);
          
          if (!wsConnected && !longPollActive) {
            startLongPollFallback();
          }
          
          setTimeout(startWebSocketEngine, wsReconnectDelay);
          wsReconnectDelay = Math.min(wsReconnectDelay * 2, WS_MAX_RECONNECT_DELAY);
        };
      } catch (err: any) {
        if (!isTornDown) {
          setTimeout(startWebSocketEngine, wsReconnectDelay);
          wsReconnectDelay = Math.min(wsReconnectDelay * 2, WS_MAX_RECONNECT_DELAY);
        }
      }
    };

    startWebSocketEngine();

    return () => {
      isTornDown = true;
      DirectMesh.registerWebSocket(null);
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
