import { useEffect, useRef, useState, useCallback } from 'react';
import { Platform, AppState, AppStateStatus, ToastAndroid, NativeModules } from 'react-native';
import * as Clipboard from 'expo-clipboard';
import * as FileSystem from 'expo-file-system/legacy';
import * as MediaLibrary from 'expo-media-library';
import { syncLog } from '../../utils/debugLog';
import { fetchWithTimeout, resolveOptimalUrl } from '../../utils/networkHelpers';
import { NetworkClock } from '../../utils/networkClock';
import { ClipItem, SYNC_CACHE_BASE, IMAGE_CACHE_BASE } from '../../utils/clipTypes';

const { AdvanceOverlay } = NativeModules;

/**
 * Extracted from index.tsx SyncScreen (lines 660-849).
 *
 * Fixes:
 *   H6 — Merges dual screenshot polling (3s + 2s) into single 3s unified loop
 *   C2 — Reduces index.tsx by ~200 lines
 *
 * This hook handles:
 *   1. Foreground clipboard detection (on AppState change)
 *   2. Screenshot detection via MediaLibrary polling
 *   3. Screenshot detection via native ScreenshotObserver (AdvanceOverlay)
 *   4. Screenshot upload to PC via LAN/Cloudflare
 */
interface UseScreenshotSyncParams {
  deviceName: string;
  isGlobalSyncEnabled: boolean;
  isFloatingBallEnabled: boolean;
  activeDevices: any[];
  pcLocalIp: string;
  pairingKeyRef: React.MutableRefObject<string>;
  sentContentFingerprintsRef: React.MutableRefObject<Set<string>>;
  processedEventsRef: React.MutableRefObject<Map<string, number>>;
  localScreenshotsRef: React.MutableRefObject<ClipItem[]>;
  lastWorkingPcUrlRef: React.MutableRefObject<string | null>;
  cachedPcUrlRef: React.MutableRefObject<string | null>;
  getCachedPcUrl: () => Promise<string | null>;
  setClips: React.Dispatch<React.SetStateAction<ClipItem[]>>;
  setIsSending: React.Dispatch<React.SetStateAction<boolean>>;
  scrollToTop: () => void;
  transmitTextSecurely: (text: string) => Promise<void>;
  lastCopiedRef: React.MutableRefObject<string>;
  setLastCopiedText: React.Dispatch<React.SetStateAction<string>>;
  normalizeTextForFingerprint: (text: string) => string;
  MAX_CLIPS_IN_MEMORY: number;
  /** A-10: when false, skip screenshot polling (not clipboard). Defaults to true. */
  isFocused?: boolean;
}

export function useScreenshotSync(params: UseScreenshotSyncParams) {
  const {
    deviceName,
    isGlobalSyncEnabled,
    isFloatingBallEnabled,
    activeDevices,
    pcLocalIp,
    pairingKeyRef,
    sentContentFingerprintsRef,
    processedEventsRef,
    localScreenshotsRef,
    lastWorkingPcUrlRef,
    cachedPcUrlRef,
    getCachedPcUrl,
    setClips,
    setIsSending,
    scrollToTop,
    transmitTextSecurely,
    lastCopiedRef,
    setLastCopiedText,
    normalizeTextForFingerprint,
    MAX_CLIPS_IN_MEMORY,
    isFocused = true,
  } = params;

  const [lastScannedImageId, setLastScannedImageId] = useState<string | null>(null);
  const lastSyncedScreenshotRef = useRef<string | null>(null);
  const lastProcessedScreenshotRef = useRef<string | null>(null);
  // M-1 FIX: Rate limiting for screenshot uploads
  const lastScreenshotUploadTimeRef = useRef<number>(0);
  const MIN_SCREENSHOT_UPLOAD_INTERVAL_MS = 2000; // 2s minimum between uploads
  const mediaPermGrantedRef = useRef<boolean>(false);

  // A-2 fix: stabilise activeDevices via ref to prevent resolvePcUrl/main-effect churn
  const activeDevicesRef = useRef(activeDevices);
  useEffect(() => { activeDevicesRef.current = activeDevices; }, [activeDevices]);

  // A-3 fix: stabilise lastScannedImageId via ref to prevent handleForegroundMediaCheck re-creation
  const lastScannedImageIdRef = useRef(lastScannedImageId);
  useEffect(() => { lastScannedImageIdRef.current = lastScannedImageId; }, [lastScannedImageId]);

  // ─── Clipboard foreground check ───
  const handleForegroundClipboardCheck = useCallback(async () => {
    if (Platform.OS === 'web') return;
    try {
      const hasText = await Clipboard.hasStringAsync();
      if (hasText) {
        const text = await Clipboard.getStringAsync();
        if (text && text.startsWith('flyshelf://')) return;
        // M-7 FIX: Skip sensitive clipboard content (OTPs, short numeric codes)
        const trimmed = text?.trim() || '';
        if (trimmed && /^\d{4,8}$/.test(trimmed)) {
          syncLog('CLIPBOARD', 'Skipped OTP-like clipboard content');
          return;
        }
        const normText = normalizeTextForFingerprint(text);
        const normLastCopied = normalizeTextForFingerprint(lastCopiedRef.current || '');
        if (normText && normText !== normLastCopied) {
          lastCopiedRef.current = text;
          setLastCopiedText(text);
          await transmitTextSecurely(text);
        }
      }
    } catch (e: any) {
      syncLog('CLIPBOARD', `Foreground check failed: ${e?.message || e}`);
    }
  }, [normalizeTextForFingerprint, transmitTextSecurely, lastCopiedRef, setLastCopiedText]);

  // ─── Resolve PC URL helper ───
  const resolvePcUrl = useCallback(async (): Promise<string | null> => {
    const activePc = activeDevicesRef.current.find((d: any) => d.DeviceType === 'PC');
    let targetUrl = activePc
      ? (activePc._lanVerified && activePc._lanUrl)
        ? activePc._lanUrl
        : await resolveOptimalUrl(activePc)
      : await getCachedPcUrl();

    if (!targetUrl) {
      if (lastWorkingPcUrlRef.current) {
        targetUrl = lastWorkingPcUrlRef.current;
      } else if (pcLocalIp?.trim()) {
        const rawParts = pcLocalIp.split(',').map(s => s.trim()).filter(Boolean);
        if (rawParts.length > 0) {
          const raw = rawParts[0];
          targetUrl = raw.startsWith('http') ? raw.replace(/\/$/, '') : `http://${raw.includes(':') ? raw : raw + ':8999'}`;
        }
      }
    }
    return targetUrl;
  }, [getCachedPcUrl, lastWorkingPcUrlRef, pcLocalIp]);

  // ─── Native ScreenshotObserver poll ───
  const pollAndSyncScreenshot = useCallback(async () => {
    if (Platform.OS !== 'android' || !AdvanceOverlay) return;
    try {
      const result = await AdvanceOverlay.getLatestScreenshot();
      const screenshotPath = typeof result === 'string' ? result : result?.path;
      if (screenshotPath && screenshotPath !== lastSyncedScreenshotRef.current) {
        const fileName = screenshotPath.split('/').pop() || '';
        if (sentContentFingerprintsRef.current.has(`screenshot::${fileName}`)) {
          lastSyncedScreenshotRef.current = screenshotPath;
          return;
        }
        // M-1 FIX: Rate limit screenshot uploads
        const now = Date.now();
        if (now - lastScreenshotUploadTimeRef.current < MIN_SCREENSHOT_UPLOAD_INTERVAL_MS) {
          syncLog('SCREENSHOT', `Rate limited: ${fileName} (too soon after last upload)`);
          return;
        }
        lastScreenshotUploadTimeRef.current = now;
        sentContentFingerprintsRef.current.add(`screenshot::${fileName}`);
        // M-9 FIX: Cap sentContentFingerprints to prevent unbounded growth
        if (sentContentFingerprintsRef.current.size > 500) {
          const entries = Array.from(sentContentFingerprintsRef.current);
          sentContentFingerprintsRef.current = new Set(entries.slice(-200));
        }
        lastSyncedScreenshotRef.current = screenshotPath;
        syncLog('SCREENSHOT', `Native detected: ${fileName}`);

        const targetUrl = await resolvePcUrl();
        if (targetUrl) {
          const uploadUri = screenshotPath.startsWith('file://') ? screenshotPath : `file://${screenshotPath}`;
          try {
            const upRes = await FileSystem.uploadAsync(
              `${targetUrl}/api/sync_file?name=${encodeURIComponent(fileName)}&type=Image&sourceDevice=${encodeURIComponent(deviceName || 'Mobile')}`,
              uploadUri,
              {
                httpMethod: 'POST',
                uploadType: 0 as any,
                headers: {
                  'X-Original-Date': NetworkClock.now().toString(),
                  'X-FlyShelf-Client': 'MobileCompanion',
                  ...(pairingKeyRef.current ? { 'X-Pairing-Key': pairingKeyRef.current } : {}),
                },
              }
            );
            if (upRes.status === 200) {
              syncLog('SCREENSHOT', `Sent to PC via ${targetUrl.includes('trycloudflare') ? 'Cloud' : 'LAN'}: ${fileName}`);
              if (Platform.OS === 'android') ToastAndroid.show(`Screenshot synced to PC ✨`, ToastAndroid.SHORT);
            }
          } catch (e: any) {
            syncLog('SCREENSHOT', `Upload failed: ${e?.message}`);
          }
        }
      }
    } catch (e) {
      console.warn('pollAndSyncScreenshot: error', (e as any)?.message || e);
    }
  }, [deviceName, pairingKeyRef, sentContentFingerprintsRef, resolvePcUrl]);

  // ─── MediaLibrary screenshot detection ───
  const handleForegroundMediaCheck = useCallback(async () => {
    try {
      if (!mediaPermGrantedRef.current) {
        let perm = await MediaLibrary.getPermissionsAsync();
        if (perm.status !== 'granted') {
          perm = await MediaLibrary.requestPermissionsAsync();
          if (perm.status !== 'granted') return;
        }
        mediaPermGrantedRef.current = true;
      }
      const media = await MediaLibrary.getAssetsAsync({
        first: 1,
        mediaType: ['photo'],
        sortBy: [[MediaLibrary.SortBy.creationTime, false]],
      });
      if (media.assets.length > 0) {
        const latest = media.assets[0];
        const isRecent = (NetworkClock.now() - latest.creationTime) < 2 * 60 * 1000;
        const isScreenshot = (latest.filename || '').toLowerCase().includes('screenshot');
        if (isRecent && isScreenshot && latest.id !== lastScannedImageIdRef.current) {
          if (lastProcessedScreenshotRef.current === latest.id) return;
          lastProcessedScreenshotRef.current = latest.id;
          const fp = `screenshot::${latest.filename}`;
          if (sentContentFingerprintsRef.current.has(fp)) {
            setLastScannedImageId(latest.id);
            return;
          }
          setLastScannedImageId(latest.id);
          sentContentFingerprintsRef.current.add(fp);
          // M-9 FIX: Cap sentContentFingerprints to prevent unbounded growth
          if (sentContentFingerprintsRef.current.size > 500) {
            const entries = Array.from(sentContentFingerprintsRef.current);
            sentContentFingerprintsRef.current = new Set(entries.slice(-200));
          }
          setIsSending(true);
          syncLog('MEDIA', `Screenshot detected: ${latest.filename}`);
          try {
            const assetInfo = await MediaLibrary.getAssetInfoAsync(latest.id);
            const assetUri = assetInfo.localUri || assetInfo.uri;
            if (assetUri) {
              const safeName = (assetInfo.filename || `ss_${NetworkClock.now()}.png`).replace(/[^a-zA-Z0-9.-]/g, '_');
              await FileSystem.makeDirectoryAsync(IMAGE_CACHE_BASE, { intermediates: true }).catch(() => {});
              const localCopy = `${IMAGE_CACHE_BASE}${safeName}`;
              try { await FileSystem.copyAsync({ from: assetUri, to: localCopy }); } catch { /* use asset URI directly */ }
              const previewUri = localCopy;

              const screenshotItem: ClipItem = {
                Title: assetInfo.filename || safeName,
                Type: 'ImageLink',
                Raw: previewUri,
                CachedUri: previewUri,
                Time: new Date().toLocaleString(),
                SourceDeviceName: deviceName || 'Phone',
                SourceDeviceType: 'Mobile',
                Timestamp: NetworkClock.now(),
                _receivedVia: 'Local',
              };
              localScreenshotsRef.current = [screenshotItem, ...localScreenshotsRef.current].slice(0, 10);
              setClips(prev => {
                const next = [screenshotItem, ...prev.filter(c => c.Title !== screenshotItem.Title)];
                return next.length > MAX_CLIPS_IN_MEMORY
                  ? [...next.filter(c => c.IsPinned), ...next.filter(c => !c.IsPinned)].slice(0, MAX_CLIPS_IN_MEMORY)
                  : next;
              });
              scrollToTop();

              if (Platform.OS === 'android' && AdvanceOverlay && isFloatingBallEnabled) {
                try { AdvanceOverlay.pushClipToNativeDB(previewUri, deviceName || 'Phone'); } catch {}
              }

              try {
                const base64 = await FileSystem.readAsStringAsync(previewUri, { encoding: FileSystem.EncodingType.Base64 });
                await Clipboard.setImageAsync(base64);
              } catch {}

              // Upload to PC — ONLY if native AdvanceOverlay is NOT available
              if (!AdvanceOverlay) {
                const targetUrl = await resolvePcUrl();
                let localSuccess = false;
                if (targetUrl) {
                  try {
                    const upRes = await FileSystem.uploadAsync(
                      `${targetUrl}/api/sync_file?name=${encodeURIComponent(assetInfo.filename || 'screenshot.jpg')}&type=ImageLink&sourceDevice=${encodeURIComponent(deviceName || 'Mobile')}`,
                      assetUri,
                      {
                        httpMethod: 'POST',
                        uploadType: 0 as any,
                        headers: {
                          'X-Original-Date': NetworkClock.now().toString(),
                          'X-FlyShelf-Client': 'MobileCompanion',
                          ...(pairingKeyRef.current ? { 'X-Pairing-Key': pairingKeyRef.current } : {}),
                        },
                      }
                    );
                    localSuccess = upRes.status === 200;
                    if (localSuccess) {
                      syncLog('MEDIA', `Sent to PC via ${targetUrl.includes('trycloudflare') ? 'Cloud' : 'LAN'}: ${assetInfo.filename}`);
                    }
                  } catch (e: any) {
                    syncLog('MEDIA', `Upload failed: ${e?.message}`);
                  }
                }
                if (!localSuccess) {
                  if (Platform.OS === 'android') ToastAndroid.show(`⚠️ Could not reach PC to send screenshot`, ToastAndroid.SHORT);
                } else {
                  if (Platform.OS === 'android') ToastAndroid.show(`📸 Screenshot sent to PC!`, ToastAndroid.SHORT);
                }
              } else {
                syncLog('MEDIA', `Upload delegated to native SCREENSHOT handler`);
              }
            }
          } catch (e: any) {
            syncLog('MEDIA', `Media check failed: ${e?.message || e}`);
          }
          setIsSending(false);
        }
      }
    } catch (e: any) {
      syncLog('MEDIA', `Media outer check failed: ${e?.message || e}`);
    }
  }, [
    deviceName,
    isFloatingBallEnabled,
    sentContentFingerprintsRef,
    localScreenshotsRef,
    pairingKeyRef,
    resolvePcUrl,
    setClips,
    setIsSending,
    scrollToTop,
    MAX_CLIPS_IN_MEMORY,
  ]);

  // ─── Unified polling loop (FIX H6: merged 3s + 2s into single 3s loop) ───
  useEffect(() => {
    // A-10: always run clipboard check regardless of focus
    handleForegroundClipboardCheck();
    // A-10: skip screenshot polling when tab is not focused
    if (isFocused) handleForegroundMediaCheck();

    const subscription = AppState.addEventListener('change', (nextAppState: AppStateStatus) => {
      if (nextAppState === 'active') {
        handleForegroundClipboardCheck();
        handleForegroundMediaCheck();
      }
    });

    // UNIFIED: Single 3s interval handles BOTH MediaLibrary and native polls
    // A-10: only set up screenshot polling when tab is focused
    let unifiedPollInterval: ReturnType<typeof setTimeout> | null = null;
    if (Platform.OS !== 'web' && isFocused) {
      // Use recursive setTimeout to prevent overlapping async polls
      const pollLoop = async () => {
        try {
          await handleForegroundMediaCheck();
          // Also poll native ScreenshotObserver in the same tick
          if (Platform.OS === 'android' && AdvanceOverlay) {
            await pollAndSyncScreenshot();
          }
        } catch (e) { /* ignore */ }
        // Schedule next poll AFTER this one completes
        unifiedPollInterval = setTimeout(pollLoop, 3000) as any;
      };
      unifiedPollInterval = setTimeout(pollLoop, 3000) as any;
    }

    let mediaSub: any = null;
    if (Platform.OS !== 'web' && typeof MediaLibrary.addListener === 'function') {
      mediaSub = MediaLibrary.addListener((event) => {
        if (event.hasIncrementalChanges || (event as any)?.insertedMedia?.length > 0) {
          handleForegroundMediaCheck();
        }
      });
    }

    return () => {
      subscription.remove();
      if (mediaSub) mediaSub.remove();
      if (unifiedPollInterval) clearTimeout(unifiedPollInterval);
    };
  }, [deviceName, isGlobalSyncEnabled, isFocused, handleForegroundClipboardCheck, handleForegroundMediaCheck, pollAndSyncScreenshot]);

  return { lastScannedImageId, handleForegroundClipboardCheck };
}
