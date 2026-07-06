import React, { useState, useEffect, useRef, useCallback, useMemo } from 'react';
import { View, Text, TextInput, TouchableOpacity, ActivityIndicator, KeyboardAvoidingView, Platform, Alert, AppState, AppStateStatus, Modal, ToastAndroid, NativeModules, ScrollView } from 'react-native';
// SafeAreaView removed — ScreenHeader handles safe area
import { FlashList } from '@shopify/flash-list';
const FlashListCast = FlashList as React.ComponentType<any>;
import { LinearGradient } from 'expo-linear-gradient';
import * as Sharing from 'expo-sharing';
import * as IntentLauncher from 'expo-intent-launcher';
import { useSettings } from '../../context/SettingsContext';
import { Ionicons } from '@expo/vector-icons';
import { database, auth, ensureFirebaseAuth, getFirebaseIdToken, firebaseDatabaseUrl } from '../../firebaseConfig';
import { syncLog } from '../../utils/debugLog';
import { ref, push, set, get, onValue, query, limitToLast, orderByChild, update, remove } from 'firebase/database';
import * as DocumentPicker from 'expo-document-picker';
import * as Clipboard from 'expo-clipboard';
import * as FileSystem from 'expo-file-system/legacy';
import { getSecureItem, setSecureItem, removeSecureItem } from '../../utils/secureStorage';
import * as MediaLibrary from 'expo-media-library';
import { Image } from 'expo-image';

import * as Crypto from 'expo-crypto';

import * as Linking from 'expo-linking';
import * as ImagePicker from 'expo-image-picker';
import { CameraView, useCameraPermissions } from 'expo-camera';
import AsyncStorage from '@react-native-async-storage/async-storage';
import * as Notifications from 'expo-notifications';
import NetInfo from '@react-native-community/netinfo';


// ═══ Extracted Modules ═══
import { ClipItem, DOWNLOAD_BASE, SYNC_CACHE_BASE, CONVERTED_BASE, IMAGE_CACHE_BASE, getDownloadPath } from '../../utils/clipTypes';
import { fetchWithTimeout, getConnectionType, connectionColors, resolveOptimalUrl, getDeviceUrls, getMediaUrl, decryptDevice, decryptDeviceList, isValidPairingKey, isValidDeviceUrl } from '../../utils/networkHelpers';
import { encrypt as aesEncrypt, decrypt as aesDecrypt } from '../../utils/syncCrypto';
import { NetworkClock } from '../../utils/networkClock';
import { createSyncStyles } from '../../styles/syncStyles';
import { font, radius, space, component } from '../../styles/theme';
import { useAppTheme } from '../../hooks/useAppTheme';
import AnimatedCard from '../../components/AnimatedCard';
import RAnimated, { useSharedValue, useAnimatedScrollHandler } from 'react-native-reanimated';
import ScreenHeader from '../../components/ScreenHeader';

import CachedImage from '../../components/CachedImage';
import PdfPageEditor from '../../components/PdfPageEditor';
import { usePdfEditor } from '../../hooks/usePdfEditor';
import { useMultiSelect } from '../../hooks/useMultiSelect';
import { usePairing } from '../../hooks/usePairing';
import { useModals } from '../../hooks/useModals';
import { usePcUrlResolver } from '../../hooks/usePcUrlResolver';
import OnboardingWizard from '../../components/OnboardingWizard';
import { ActiveDevice } from '../../components/DeviceHub';
import { mergePdfs as localMergePdfs, convertImageToPdf as localConvertImageToPdf } from '../../utils/pdfUtils';

const { AdvanceOverlay } = NativeModules;

const normalizeTextForFingerprint = (text: string): string => {
  if (!text) return '';
  return text.replace(/\r\n/g, '\n').replace(/\r/g, '\n').trim();
};

/**
 * Hermes-safe timeout signal — AbortSignal.timeout() is NOT available in
 * all Hermes versions, causing "undefined is not a function" crashes.
 * Returns both the signal and a clear function to prevent timer leaks (C-3 fix).
 */
function createTimeoutSignal(ms: number): AbortSignal {
  const controller = new AbortController();
  const timerId = setTimeout(() => controller.abort(), ms);
  // Attach clear function to the signal for cleanup
  (controller.signal as any)._clearTimeout = () => clearTimeout(timerId);
  return controller.signal;
}
/** Clear the timeout associated with a createTimeoutSignal signal */
function clearTimeoutSignal(signal: AbortSignal): void {
  if ((signal as any)?._clearTimeout) (signal as any)._clearTimeout();
}

// ════════════════════════════════════════════════════════
// MAIN SCREEN
// ════════════════════════════════════════════════════════
export default function SyncScreen() {
  const { colors, shadows } = useAppTheme();
  const styles = useMemo(() => createSyncStyles(colors, shadows), [colors, shadows]);
  const { pcLocalIp, deviceName, setDeviceName, isGlobalSyncEnabled, setGlobalSyncEnabled, isFloatingBallEnabled, addPairedDevice, pairedDevices, updatePairedDeviceLicensing, updateDeviceStatus, pairingKey: contextPairingKey, regeneratePairingKey, getSyncPrefsForDevice } = useSettings();

  const isPairedPcPro = pairedDevices.some(d => d.deviceType === 'PC' && d.isPro);

  useEffect(() => {
    if (Platform.OS === 'android' && AdvanceOverlay && isFloatingBallEnabled) {
      AdvanceOverlay.startOverlay();
    }
  }, [isFloatingBallEnabled]);

  // ─── Ghost Wipe Filter State ───
  const [localWipeTimestamp, setLocalWipeTimestamp] = useState<number>(0);
  const [localDeletedIds, setLocalDeletedIds] = useState<Set<string>>(new Set());

  // ─── Core State ───
  const [clips, setClips] = useState<ClipItem[]>([]);
  // ─── Ref mirror for clips state — used by effects that must READ clips without DEPENDING on clips ───
  const clipsStateRef = useRef<ClipItem[]>([]);
  useEffect(() => { clipsStateRef.current = clips; }, [clips]);
  const feedListRef = useRef<any>(null);
  // ─── Feed Filter State ───
  type FeedCategory = 'All' | 'Text' | 'Images' | 'Docs';
  const [feedCategory, setFeedCategory] = useState<FeedCategory>('All');
  const [feedSearch, setFeedSearch] = useState('');
  // Scroll feed to top — called after any setClips that prepends a new item
  const scrollToTop = useCallback(() => {
    setTimeout(() => {
      try { feedListRef.current?.scrollToOffset({ offset: 0, animated: true }); } catch {}
    }, 300);
  }, []);
  const hasLoadedOnceRef = useRef<boolean>(false);
  // Trigger states for background download effects (fixes stale [] deps)
  const [imageDownloadTrigger, setImageDownloadTrigger] = useState(0);
  const [richMediaDownloadTrigger, setRichMediaDownloadTrigger] = useState(0);
  const lastActivityRef = useRef<number>(Date.now());
  // Cloudflare failure tracking — delegated to usePcUrlResolver hook
  const lastSyncedContentRef = useRef<string>('');
  const lastSyncedImageTsRef = useRef<number>(0);
  const sentContentFingerprintsRef = useRef<Set<string>>(new Set());
  const recentSyncFingerprintsRef = useRef<Map<string, number>>(new Map());
  // EventId-based dedup: deterministic IDs prevent echo loops without content collisions
  const processedEventsRef = useRef<Map<string, number>>(new Map());
  // C-10: Periodic cleanup of processedEventsRef to prevent unbounded growth
  useEffect(() => {
    const cleanup = setInterval(() => {
      const now = Date.now();
      const maxAge = 10 * 60 * 1000; // 10 minutes
      processedEventsRef.current.forEach((ts, key) => {
        if (now - ts > maxAge) processedEventsRef.current.delete(key);
      });
    }, 5 * 60 * 1000); // Run every 5 minutes
    return () => clearInterval(cleanup);
  }, []);
  const deviceNameRef = useRef(deviceName);
  useEffect(() => { deviceNameRef.current = deviceName; }, [deviceName]);
  const _eventCounterRef = useRef(0);
  const generateEventId = () => `Mobile_${(deviceNameRef.current || 'phone').replace(/[^a-zA-Z0-9]/g, '')}_${Date.now()}_${(++_eventCounterRef.current).toString(36)}`;
  // Track items already pushed to native overlay DB
  const pushedToOverlayRef = useRef<Set<string>>(new Set());
  // ─── Pairing Timestamp: Only sync items NEWER than when this device first paired ───
  const pairingTimestampRef = useRef<number>(0);

  // ─── Clip Persistence: Survive app restarts ───
  const CLIPS_STORAGE_KEY = '@flyshelf_clips';
  const clipPersistTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const clipsInitializedRef = useRef<boolean>(false);
  const connectionPollRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const connectionTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    return () => {
      if (connectionPollRef.current) clearInterval(connectionPollRef.current);
      if (connectionTimeoutRef.current) clearTimeout(connectionTimeoutRef.current);
    };
  }, []);

  // Debounced persist: save clips to AsyncStorage 800ms after last change
  const persistClips = useCallback((clipsToSave: ClipItem[]) => {
    if (clipPersistTimerRef.current) clearTimeout(clipPersistTimerRef.current);
    clipPersistTimerRef.current = setTimeout(() => {
      try {
        // Keep last 50 items max, include CachedUri for offline rendering
        const toSave = clipsToSave.slice(0, 50).map(c => ({
          id: c.id, Title: c.Title, Type: c.Type, Raw: c.Raw,
          Time: c.Time, Timestamp: c.Timestamp,
          SourceDeviceName: c.SourceDeviceName, SourceDeviceType: c.SourceDeviceType,
          CachedUri: c.CachedUri || undefined,
          DownloadUrl: (c as any).DownloadUrl || undefined,
          PreviewUrl: (c as any).PreviewUrl || undefined,
          IsPinned: c.IsPinned || undefined,
        }));
        AsyncStorage.setItem(CLIPS_STORAGE_KEY, JSON.stringify(toSave)).catch(() => {});
      } catch (e) { syncLog('PERSIST', `Clip persist failed: ${(e as any)?.message || e}`); }
    }, 800);
  }, []);

  // Auto-persist whenever clips change (but not on initial empty state)
  useEffect(() => {
    if (clipsInitializedRef.current && clips.length > 0) {
      persistClips(clips);
    }
  }, [clips, persistClips]);
  // ─── Firebase Anonymous Auth — sign in once at startup ───
  useEffect(() => {
    NetworkClock.sync().catch(() => {});
    ensureFirebaseAuth().then(() => {
      syncLog('[Auth] Firebase anonymous auth ready');
    }).catch((err: any) => {
      syncLog('[Auth] Firebase anonymous auth failed: ' + err?.message);
    });
    // Load pairing timestamp — items older than this are from before we paired
    AsyncStorage.getItem('pairingTimestamp').then(val => {
      if (val) pairingTimestampRef.current = parseInt(val, 10) || 0;
    });
  }, []);

  // Load persisted clips on startup + validate CachedUri files
  useEffect(() => {
    (async () => {
      try {
        const stored = await AsyncStorage.getItem(CLIPS_STORAGE_KEY);
        if (stored) {
          const parsed: ClipItem[] = JSON.parse(stored);
          // Validate CachedUri: check if the local file still exists
          const validated = await Promise.all(parsed.map(async (c) => {
            if (c.CachedUri) {
              try {
                const uri = c.CachedUri;
                const info = await FileSystem.getInfoAsync(uri);
                if (info.exists && (info as any).size > 100) {
                  return c; // File still on disk — use it
                }
              } catch {}
              // File gone — strip CachedUri, mark for re-download
              return { ...c, CachedUri: undefined, _needsDownload: true } as any;
            }
            // Images without CachedUri always need download
            if ((c.Type === 'Image' || c.Type === 'ImageLink' || c.Type === 'QRCode') && !c.CachedUri) {
              return { ...c, _needsDownload: true } as any;
            }
            return c;
          }));
          if (validated.length > 0) {
            // Sanitize file-type items: ensure Raw shows filename, not PC file paths
            const sanitized = validated.map(c => {
              if (['Pdf', 'Document', 'File', 'Video', 'Audio', 'Archive', 'Presentation'].includes(c.Type)) {
                const rawStr = c.Raw || '';
                if (/^[A-Z]:\\/.test(rawStr) || /^file:\/\/\/[A-Z]:/.test(rawStr) || rawStr.startsWith('http')) {
                  return { ...c, Raw: c.Title || c.Raw };
                }
              }
              return c;
            });
            setClips(sanitized);
            syncLog('PERSIST', `Loaded ${sanitized.length} clips from local storage`);
          }
        }
      } catch (e) {
        syncLog('PERSIST', `Load error: ${e}`);
      }
      clipsInitializedRef.current = true;
      hasLoadedOnceRef.current = true;
    })();
  }, []);

  // ─── Download Queue: Sequential file download processor ───
  interface DownloadQueueItem {
    id: string;
    title: string;
    type: string;
    fileUrl: string;
    destPath: string;
    source: string;
    sourceDevice: string;
    retryCount?: number;
    timestamp?: number;
  }
  const downloadQueueRef = useRef<DownloadQueueItem[]>([]);
  const isDownloadingRef = useRef<boolean>(false);
  const processedDownloadsRef = useRef<Set<string>>(new Set());

  const enqueueDownload = useCallback((item: DownloadQueueItem) => {
    // Dedup: don't re-enqueue already-processed or already-queued items
    const dedupKey = `${item.title}::${item.timestamp || item.fileUrl}`;
    if (processedDownloadsRef.current.has(dedupKey)) return;
    if (downloadQueueRef.current.some(q => `${q.title}::${q.timestamp || q.fileUrl}` === dedupKey)) return;
    downloadQueueRef.current.push(item);
    processDownloadQueue();
  }, []);

  const processDownloadQueue = useCallback(async () => {
    if (isDownloadingRef.current) return; // Already processing
    if (downloadQueueRef.current.length === 0) return;
    isDownloadingRef.current = true;

    while (downloadQueueRef.current.length > 0) {
      const item = downloadQueueRef.current.shift()!;
      const dedupKey = `${item.title}::${item.timestamp || item.fileUrl}`;
      if (processedDownloadsRef.current.has(dedupKey)) continue;

      const progressId = `dl_queue_${Date.now()}_${Math.random().toString(36).substr(2, 4)}`;
      try {
        // Check if already downloaded
        const existing = await FileSystem.getInfoAsync(item.destPath);
        if (existing.exists && (existing as any).size > 100) {
          syncLog('DL-QUEUE', `Skip (exists): ${item.title}`);
          setDownloadedItems(prev => { const n = new Set(prev); n.add(item.id || item.title); return n; });
          // Update clip with CachedUri
          setClips(prev => prev.map(c =>
            (c.id === item.id || c.Title === item.title) ? { ...c, CachedUri: item.destPath } : c
          ));
          continue;
        }

        // Show progress card
        setClips(prev => [{
          id: progressId, Title: `⬇️ ${item.title}`, Type: 'Text',
          Raw: `Downloading from ${item.sourceDevice} via ${item.source}...`,
          Time: new Date().toLocaleTimeString(),
        }, ...prev]);

        syncLog('DL-QUEUE', `Downloading: ${item.title} via ${item.source}`);
        const dlHeaders: Record<string, string> = { 'X-FlyShelf-Client': 'MobileCompanion' };
        if (pairingKeyRef.current) dlHeaders['X-Pairing-Key'] = pairingKeyRef.current;

        // Retry loop: 2 attempts with URL re-resolution on failure
        let queueDlSuccess = false;
        let currentFileUrl = item.fileUrl;
        for (let queueAttempt = 0; queueAttempt < 2 && !queueDlSuccess; queueAttempt++) {
          try {
            // Re-resolve URL on retry (tunnel URL may have changed)
            if (queueAttempt > 0 && currentFileUrl.includes('trycloudflare.com')) {
              try {
                const freshBase = await getCachedPcUrl();
                if (freshBase && currentFileUrl.includes('?')) {
                  const queryPart = currentFileUrl.substring(currentFileUrl.indexOf('?'));
                  currentFileUrl = `${freshBase}/download${queryPart}`;
                } else if (freshBase) {
                  currentFileUrl = freshBase;
                }
              } catch (e) { console.warn('DL-Queue URL resolve: error', (e as any)?.message || e); }
              syncLog('DL-QUEUE', `Retry #${queueAttempt}: ${currentFileUrl.substring(0, 80)}`);
            }
            // 60s timeout prevents stalled downloads from blocking the entire queue forever
            const dlResult = await Promise.race([
              FileSystem.downloadAsync(currentFileUrl, item.destPath, { headers: dlHeaders }),
              new Promise<never>((_, reject) => setTimeout(() => reject(new Error('Download timeout (60s)')), 60000)),
            ]);

            if (dlResult && dlResult.status === 200) {
              queueDlSuccess = true;
            } else {
              throw new Error(`HTTP ${dlResult?.status}`);
            }
          } catch (retryErr: any) {
            if (queueAttempt >= 1) {
              syncLog('DL-QUEUE', `❌ ${item.title} failed after 2 attempts: ${retryErr?.message || retryErr}`);
            }
          }
        }

        // Remove progress card
        setClips(prev => prev.filter(c => c.id !== progressId));

        if (queueDlSuccess) {
          processedDownloadsRef.current.add(dedupKey);
          syncLog('DL-QUEUE', `✅ ${item.title} saved via ${item.source}`);
          setDownloadedItems(prev => { const n = new Set(prev); n.add(item.id || item.title); return n; });
          // Update clip with CachedUri
          setClips(prev => prev.map(c =>
            (c.id === item.id || c.Title === item.title) ? { ...c, CachedUri: item.destPath } : c
          ));
          if (Platform.OS === 'android') ToastAndroid.show(`✅ ${item.title} saved`, ToastAndroid.SHORT);
          Notifications.scheduleNotificationAsync({
            content: { title: '📁 File Downloaded', body: `${item.title} saved successfully` },
            trigger: null,
          }).catch(() => {});
          if (item.id) { try { await markFileDownloaded(item.id); } catch {} }
        } else {
          const retries = item.retryCount || 0;
          if (retries < 3) {
            // Exponential backoff: 2s, 5s, 10s
            const backoffDelays = [2000, 5000, 10000];
            const delay = backoffDelays[retries] || 10000;
            syncLog('DL-QUEUE', `⏳ ${item.title} failed attempt ${retries + 1}/3, retrying in ${delay / 1000}s...`);
            await new Promise(r => setTimeout(r, delay));
            item.retryCount = retries + 1;
            downloadQueueRef.current.push(item);
          } else {
            syncLog('DL-QUEUE', `❌ ${item.title} permanently failed after 3 retries`);
          }
          await FileSystem.deleteAsync(item.destPath, { idempotent: true }).catch(() => {});
        }
      } catch (err: any) {
        syncLog('DL-QUEUE', `❌ ${item.title} error: ${err?.message || err}`);
        setClips(prev => prev.filter(c => c.id !== progressId));
        await FileSystem.deleteAsync(item.destPath, { idempotent: true }).catch(() => {});
      }
    }
    isDownloadingRef.current = false;
    // Cap processed set using sliding slice eviction to prevent unbounded memory growth while keeping history
    if (processedDownloadsRef.current.size > 500) {
      const items = Array.from(processedDownloadsRef.current);
      processedDownloadsRef.current = new Set(items.slice(-200));
    }
  }, []);

  // ─── downloadedBy Tracking: Mark file as downloaded by this device ───
  const markFileDownloaded = async (entryId: string) => {
    try {
      const pk = pairingKeyRef.current;
      if (!pk || !entryId) return;
      const myDeviceId = `Mobile_${(deviceName || 'phone').replace(/\s/g, '_')}`;

      // Step 1: Mark this device as downloaded
      await set(ref(database, `clipboard/${pk}/${entryId}/downloadedBy/${myDeviceId}`), NetworkClock.now());

      // Step 2: Read the full entry to check targets
      const snap = await get(ref(database, `clipboard/${pk}/${entryId}`));
      if (!snap.exists()) return;
      const data = snap.val();

      const targets: string[] = data.targetDevices || [];
      const downloaded = Object.keys(data.downloadedBy || {});

      if (targets.length === 0) {
        // Legacy entry or no targets — just delete
        await set(ref(database, `clipboard/${pk}/${entryId}`), null);
        return;
      }

      // Step 3: Check which remaining targets are offline → mark them done
      const remaining = targets.filter((t: string) => !downloaded.includes(t));
      if (remaining.length > 0) {
        try {
          const devSnap = await get(ref(database, `active_devices/${pk}`));
          if (devSnap.exists()) {
            const devices = devSnap.val();
            const now = NetworkClock.now();
            const onlineIds = new Set<string>();
            Object.values(devices).forEach((dev: any) => {
              if (dev.IsOnline && (now - (dev.Timestamp || 0)) < 300000) {
                onlineIds.add(dev.DeviceId || '');
              }
            });
            // Mark offline devices as done
            for (const offId of remaining.filter((r: string) => !onlineIds.has(r))) {
              await set(ref(database, `clipboard/${pk}/${entryId}/downloadedBy/${offId}`), -1);
              downloaded.push(offId);
            }
          }
        } catch (e) { console.warn('Firebase download tracking: error', (e as any)?.message || e); }
      }

      // Step 4: If all targets downloaded → delete entry
      if (targets.every((t: string) => downloaded.includes(t))) {
        await set(ref(database, `clipboard/${pk}/${entryId}`), null);
        syncLog(`[SYNC_CLEANUP] All ${targets.length} devices done — entry deleted`);
      } else {
        syncLog(`[SYNC_TRACK] ${downloaded.length}/${targets.length} devices done`);
      }
    } catch (e) { syncLog(`[SYNC_TRACK] markFileDownloaded error: ${e}`); }
  };

  // Scoped Clipboard (only paired devices see each other)
  const pairingKeyRef = useRef<string>('');
  useEffect(() => {
    getSecureItem('pairingKey').then(k => {
      if (isValidPairingKey(k)) {
        pairingKeyRef.current = k!;
        if (Platform.OS === 'android' && AdvanceOverlay?.setPairingKey) AdvanceOverlay.setPairingKey(k!);
      }
    });
  }, []);
  // Keep ref in sync when context key changes (e.g. after pairing or regeneration)
  useEffect(() => {
    if (isValidPairingKey(contextPairingKey)) {
      pairingKeyRef.current = contextPairingKey;
      if (Platform.OS === 'android' && AdvanceOverlay?.setPairingKey) AdvanceOverlay.setPairingKey(contextPairingKey);
    }
  }, [contextPairingKey]);
  /** Returns the Firebase path scoped to the pairing key, e.g. `clipboard/abc123` */
  const clipboardPath = () => {
    const pk = pairingKeyRef.current;
    if (!isValidPairingKey(pk)) {
      throw new Error("Invalid or missing pairing key room scope");
    }
    return `clipboard/${pk}`;
  };

  // Extracted to usePcUrlResolver hook (C1 decomposition)
  // NOTE: Hook call is below after activeDevicesRef declaration (~line 525)

  // ─── Overlay Sync ───
  const lastNativeSyncRef = useRef<number>(0);
  const overlaySyncTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => {
    if (Platform.OS === 'android' && AdvanceOverlay && isFloatingBallEnabled) {
      // Debounce overlay sync: wait 500ms after last clips change before syncing
      if (overlaySyncTimerRef.current) clearTimeout(overlaySyncTimerRef.current);
      overlaySyncTimerRef.current = setTimeout(() => {
        const now = NetworkClock.now();
        lastNativeSyncRef.current = now;

        const currentClips = clipsStateRef.current;
        const filtered = currentClips.filter(c => (c.IsPinned || (c.Timestamp || 0) >= localWipeTimestamp) && (!c.id || !localDeletedIds.has(c.id)) && (c.Raw || c.Title));
        const seen = new Set<string>();
        const deduped = filtered.filter(c => {
          const key = (c.Raw || c.Title || '').substring(0, 200);
          if (seen.has(key)) return false;
          seen.add(key);
          return true;
        });
        if (deduped.length > 0) {
          // Only push items NOT already in native DB
          const mapped = deduped.slice(0, 20).filter(c => {
            const overlayFp = `overlay::${(c.Raw || c.Title || '').substring(0, 100)}`;
            if (pushedToOverlayRef.current.has(overlayFp)) return false;
            pushedToOverlayRef.current.add(overlayFp);
            return true;
          }).map(c => {
            let rawData = c.Raw;
            if (c.Type === 'Pdf' || c.Type === 'Document' || c.Type === 'Archive') {
              const safeName = c.Title.replace(/[^a-zA-Z0-9.-]/g, '_');
              rawData = DOWNLOAD_BASE + safeName;
            }
            return {
              Title: c.Title, Raw: rawData || '', Type: c.Type || 'Text',
              SourceDeviceName: c.SourceDeviceName || 'Cloud', Timestamp: c.Timestamp,
              DownloadUrl: c.Raw?.startsWith?.('http') ? c.Raw : '',
            };
          });
          if (mapped.length > 0) {
            try { AdvanceOverlay.syncNativeDB(JSON.stringify(mapped)); } catch(e: any) { console.warn('Overlay syncNativeDB: error', e?.message || e); }
          }
        }
        // Cap overlay tracker to prevent unbounded growth using sliding window slice
        if (pushedToOverlayRef.current.size > 500) {
          const items = Array.from(pushedToOverlayRef.current);
          pushedToOverlayRef.current = new Set(items.slice(-200));
        }
      }, 1000);
    }
    return () => { if (overlaySyncTimerRef.current) clearTimeout(overlaySyncTimerRef.current); };
  }, [clips, isFloatingBallEnabled, localWipeTimestamp, localDeletedIds]);

  // ─── Bidirectional Overlay Sync ───
  useEffect(() => {
    if (Platform.OS !== 'android' || !AdvanceOverlay || !isFloatingBallEnabled || !deviceName) return;
    // Immediately configure overlay with PC URL for seamless sync
    (async () => {
      try {
        const targetUrl = await getCachedPcUrl();
        if (targetUrl) AdvanceOverlay.setPcUrl(targetUrl);
        if (deviceName) AdvanceOverlay.setDeviceName(deviceName);
      } catch (e) { syncLog('OVERLAY', `Overlay URL config failed: ${(e as any)?.message || e}`); }
    })();
    const pollInterval = setInterval(async () => {
      try {
        const copiedText = await AdvanceOverlay.getLastCopiedFromOverlay();
        if (copiedText && copiedText.trim().length > 0) {
          // Fingerprint to prevent echo back from Firebase
          sentContentFingerprintsRef.current.add(copiedText.substring(0, 200));
          const overlayEventId = generateEventId();
          processedEventsRef.current.set(overlayEventId, NetworkClock.now());
          const newItem: ClipItem = {
            Title: copiedText.substring(0, 80), Type: 'Text', Raw: copiedText,
            Time: new Date().toLocaleString(), SourceDeviceName: deviceName,
            SourceDeviceType: 'Mobile', Timestamp: NetworkClock.now(),
            _receivedVia: 'Local',
          };
          setClips(prev => [newItem, ...prev]);
          scrollToTop();
          if (isGlobalSyncEnabled && copiedText.length <= 1_000_000) {
            try { if (pairingKeyRef.current) { const clipRef = push(ref(database, clipboardPath())); await set(clipRef, { ...newItem, EventId: overlayEventId }); } } catch(e) { syncLog('OVERLAY', `Overlay poll error: ${(e as any)?.message || e}`); }
          }
        }
      } catch(e) { syncLog('OVERLAY', `Overlay poll error: ${(e as any)?.message || e}`); }
    }, 1500);
    return () => clearInterval(pollInterval);
  }, [isFloatingBallEnabled, deviceName, isGlobalSyncEnabled]);

  // ─── Device Discovery ───
  const [activeDevices, setActiveDevices] = useState<ActiveDevice[]>([]);
  // [REMOVED] activeDevicesList — was dead state (set but never read)
  const activeDevicesRef = useRef<ActiveDevice[]>([]);
  // ─── PC URL resolver hook (moved here because it depends on activeDevicesRef) ───
  const { getCachedPcUrl, invalidateCache: invalidatePcUrlCache, recordCloudflareFailure, resetCloudflareFailCount, cachedPcUrlRef, cachedPcUrlTimestampRef, discoveryMethodRef } = usePcUrlResolver(pairingKeyRef, activeDevicesRef, pcLocalIp);
  // Keep ref in sync with state so interval callbacks never use stale data
  useEffect(() => { activeDevicesRef.current = activeDevices; }, [activeDevices]);

  // ─── Screenshot Detection ───
  // SINGLE source of truth: handled by handleForegroundMediaCheck + pollAndSyncScreenshot in the main useEffect below.
  // This avoids duplicate detectors that cause infinite loops.
  const lastScreenshotTsRef = useRef<number>(NetworkClock.now());
  // Local screenshots are stored in a ref so Firebase listener can merge them into the feed
  const localScreenshotsRef = useRef<ClipItem[]>([]);

  // ─── UI State ───
  // [REMOVED] isRefreshing — was dead state (never read)
  const [inputText, setInputText] = useState('');
  const [isSending, setIsSending] = useState(false);
  const [lastCopiedText, setLastCopiedText] = useState('');
  const [setupName, setSetupName] = useState('');
  const { isTargetModalVisible, setIsTargetModalVisible, isCameraOptionsVisible, setIsCameraOptionsVisible, isQRScannerActive, setIsQRScannerActive, expandedImage, setExpandedImage, isMergeModalVisible, setIsMergeModalVisible, mergeQueue, setMergeQueue, isForceSyncModalVisible, setIsForceSyncModalVisible, forceSyncDevices, setForceSyncDevices, isConnectModalVisible, setIsConnectModalVisible } = useModals();
  const [pendingUploadPayload, setPendingUploadPayload] = useState<{uri: string; name: string; type: string} | null>(null);
  const [uploadProgress, setUploadProgress] = useState<{ name: string; progress: number; speedMBps?: number } | null>(null);
  const [connectionInfo, setConnectionInfo] = useState<{ url: string; latencyMs: number; type: 'LAN' | 'Cloud' } | null>(null);
  const [downloadedItems, setDownloadedItems] = useState<Set<string>>(new Set());
  // [REMOVED] downloadProgress — was dead state (never read)
  const [incomingTransferProgress, setIncomingTransferProgress] = useState<{[key: string]: number}>({});
  const [cameraPermission, requestCameraPermission] = useCameraPermissions();
  const [lastScannedImageId, setLastScannedImageId] = useState<string | null>(null);
  const [latestIngestedId, setLatestIngestedId] = useState<string | null>(null);
  const [activeOptionsId, setActiveOptionsId] = useState<string | null>(null);
  const { isMultiSelectMode, selectedItemIds, toggleSelectItem, exitMultiSelect, enterMultiSelect } = useMultiSelect();
  const { pairingCodeInput, setPairingCodeInput, myPairingCode, setMyPairingCode, isPairing, setIsPairing, pairedPcName, setPairedPcName } = usePairing(pairedDevices.length);
  // ── PDF Page Editor ──
  const { pageEditorVisible, pageEditorUri, pageEditorTitle, openPageEditor, closePageEditor } = usePdfEditor();
  const [showOnboarding, setShowOnboarding] = useState(false);

  // ─── Persistence ───
  useEffect(() => {
    AsyncStorage.getItem('localWipeTimestamp').then(val => {
      if (val) { setLocalWipeTimestamp(parseInt(val, 10) || 0); }
      else { setLocalWipeTimestamp(0); AsyncStorage.setItem('localWipeTimestamp', '0'); }
    });
    AsyncStorage.getItem('localDeletedIds').then(val => {
      if (val) { try { const arr = JSON.parse(val); setLocalDeletedIds(new Set(arr.slice(-500))); } catch(e) {} }
    });
    (async () => {
      if (!(await AsyncStorage.getItem('@flyshelf_onboarding_done'))) setShowOnboarding(true);
    })();
  }, []);

  // ─── Peer Relay ───
  useEffect(() => {
    if (!deviceName) return;
    const safeDeviceName = (deviceName || 'Phone').replace(/[^a-zA-Z0-9_-]/g, '_');
    const peerRef = query(ref(database, `peer_transfers/${safeDeviceName}`));
    const unsubscribePeer = onValue(peerRef, async (snapshot) => {
      if (snapshot.exists() && Platform.OS !== 'web') {
        const data = snapshot.val();
        const updates: any = {};
        for (const key of Object.keys(data)) {
          const batch = data[key];
          if (batch.urls && Array.isArray(batch.urls)) {
            if (Platform.OS === 'android') ToastAndroid.show(`Incoming batch transfer from ${batch.sender}...`, ToastAndroid.LONG);
            try {
              const perm = await MediaLibrary.requestPermissionsAsync();
              if (perm.status === 'granted') {
                await Promise.all(batch.urls.map(async (url: string, idx: number) => {
                  const localUri = `${SYNC_CACHE_BASE}relayed_${NetworkClock.now()}_${idx}.jpg`;
                  const dl = await Promise.race([
                    FileSystem.downloadAsync(url, localUri, {
                      headers: {
                        'X-FlyShelf-Client': 'MobileCompanion',
                        'X-Pairing-Key': pairingKeyRef.current
                      }
                    }),
                    new Promise<never>((_, reject) => setTimeout(() => reject(new Error('Peer download timeout')), 60000))
                  ]);
                  const asset = await MediaLibrary.createAssetAsync(dl.uri);
                  await MediaLibrary.createAlbumAsync("FlyShelf Extractions", asset, false);
                }));
                if (Platform.OS === 'android') ToastAndroid.show("Extraction successful: Saved to Native Gallery ✅", ToastAndroid.LONG);
              }
            } catch (e) { if (Platform.OS === 'android') ToastAndroid.show("Failed to relay items to Gallery.", ToastAndroid.SHORT); }
            updates[key] = null;
          }
        }
        if (Object.keys(updates).length > 0) await update(ref(database, `peer_transfers/${safeDeviceName}`), updates);
      }
    });
    return () => unsubscribePeer();
  }, [deviceName]);

  // Helper: wrap getMediaUrl with current state (M-4: memoized to fix renderClipItem deps)
  const getMediaUrlForItem = useCallback((item: any) => getMediaUrl(item, activeDevices, pcLocalIp), [activeDevices, pcLocalIp]);


  // ─── Firebase Listeners (LAZY — Phase 1 Optimization) ───
  // The clipboard listener is now LAZY: it only activates after 30s of the PC
  // being unreachable via direct connection (LAN/Cloudflare). This eliminates
  // the persistent WebSocket that was hitting the 200-connection Firebase limit.
  const firebaseFallbackTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const firebaseUnsubFeedRef = useRef<(() => void) | null>(null);
  const firebaseUnsubNodesRef = useRef<(() => void) | null>(null);
  const lastSuccessfulPollRef = useRef<number>(NetworkClock.now());

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
            for (const p of parsed) {
              // Check ALL existing clips for duplicate by Raw content or Title
              const dupIdx = updated.findIndex(c =>
                (c.id && p.id && c.id === p.id) ||
                (c.Title && p.Title && c.Title === p.Title) ||
                (c.Raw && p.Raw && c.Raw.substring(0, 200) === p.Raw.substring(0, 200))
              );
              if (dupIdx >= 0) {
                // Duplicate found — remove from old position, put at top with updated timestamp
                const existing = updated.splice(dupIdx, 1)[0];
                updated.unshift({ ...existing, Timestamp: NetworkClock.now() });
                changed = true;
              } else {
                // Genuinely new — add to top
                updated.unshift(p);
                changed = true;
              }
            }
            // Also merge local screenshots
            const screenshots = localScreenshotsRef.current.filter(ls =>
              !updated.some(p => p.Title === ls.Title) && !parsed.some(p => p.Title === ls.Title)
            );
            if (screenshots.length > 0) {
              updated = [...screenshots, ...updated];
              changed = true;
            }
            if (!changed) return prev;
            scrollToTop();
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

    // ─── Startup Cleanup: Purge stale entries older than 1 hour (one-time) ───
    (async () => {
      try {
        const allSnap = await get(ref(database, `clipboard/${pk}`));
        if (allSnap.exists()) {
          const allData = allSnap.val();
          const now = NetworkClock.now();
          const ONE_HOUR = 60 * 60 * 1000;
          let purged = 0;
          for (const key of Object.keys(allData)) {
            const entry = allData[key];
            if (entry.Timestamp && (now - entry.Timestamp) > ONE_HOUR) {
              await set(ref(database, `clipboard/${pk}/${key}`), null);
              purged++;
            }
          }
          if (purged > 0) syncLog('CLEANUP', `Purged ${purged} stale Firebase entries (>1hr old)`);
        }
      } catch (e) { syncLog('CLEANUP', `Startup cleanup error: ${e}`); }
    })();

    // ─── Active Devices: REAL-TIME onValue listener ───
    // Catches PC URLs the instant they appear — critical because
    // v5 PC auto-deletes its URL from Firebase after 5 seconds.
    // The listener caches URLs locally so they survive deletion.
    const peerDevicesRef = ref(database, `active_devices/${pk}`);
    const unsubscribeDevices = onValue(peerDevicesRef, async (snapshot) => {
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
        const filtered = Object.keys(data).map(k => ({ ...data[k], _key: k })).filter(d => d.IsOnline && d.Timestamp && (now - d.Timestamp) < 600_000);
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
  }, [isGlobalSyncEnabled, contextPairingKey, JSON.stringify((pairedDevices || []).map((d: any) => d.deviceId || d.DeviceId).sort())]);

  // ─── Last proven-working PC URL (set by poll on successful /api/sync) ───
  const lastWorkingPcUrlRef = useRef<string | null>('');

  // ─── Background image download sweep ───
  // Watches clips for image items that need downloading (added by LAN poll or Firebase)
  // Decouples detection from download — any path can add images, this path downloads them
  const downloadingRef = useRef<Set<string>>(new Set());
  useEffect(() => {
    if (Platform.OS !== 'android') return;
    // Use ref to read clips without depending on clips (prevents infinite loop)
    const currentClips = clipsStateRef.current;
    const needsDownload = currentClips.filter(c =>
      (c.Type === 'Image' || c.Type === 'ImageLink') &&
      !c.CachedUri &&
      c.id &&
      !downloadingRef.current.has(c.id) &&
      // Has some URL to download from
      (c.Raw?.startsWith('http') || (c as any).DownloadUrl || (c as any).PreviewUrl || (c as any)._needsDownload)
    );
    if (needsDownload.length === 0) return;

    (async () => {
      for (const imgItem of needsDownload) {
        if (!imgItem.id || downloadingRef.current.has(imgItem.id)) continue;
        downloadingRef.current.add(imgItem.id);
        try {
          await FileSystem.makeDirectoryAsync(SYNC_CACHE_BASE, { intermediates: true }).catch(() => {});
          const localUri = `${SYNC_CACHE_BASE}fb_img_${imgItem.id}.png`;
          const existing = await FileSystem.getInfoAsync(localUri);
          if (existing.exists && (existing as any).size > 100) {
            setClips(prev => prev.map(c =>
              c.id === imgItem.id ? { ...c, Raw: localUri, CachedUri: localUri, _needsDownload: undefined } : c
            ));
            if (imgItem === needsDownload[0]) {
              try {
                const b64 = await FileSystem.readAsStringAsync(localUri, { encoding: FileSystem.EncodingType.Base64 });
                await Clipboard.setImageAsync(b64);
              } catch (e) { console.warn('Clipboard setImage from cache: error', (e as any)?.message || e); }
            }
            continue;
          }

          // Resolve the best download URL — prefer DownloadUrl, then Raw
          const itemAny = imgItem as any;
          const dlUrl = itemAny.DownloadUrl || itemAny.PreviewUrl || '';
          const rawUrl = imgItem.Raw || '';
          // Extract the ?path= portion for smart URL building
          const sourceUrl = dlUrl || rawUrl;
          let downloadUrl = rawUrl.startsWith('http') ? rawUrl : '';
          let downloadSource = 'Cloud';

          // FAST PATH: If the source URL is already a full HTTP URL, use it directly
          if (sourceUrl.startsWith('http') && !sourceUrl.includes('trycloudflare.com')) {
            downloadUrl = sourceUrl;
            downloadSource = 'LAN';
          } else if (sourceUrl.includes('?path=') || sourceUrl.includes('/download')) {
            const pathPart = sourceUrl.includes('?path=') ? sourceUrl.substring(sourceUrl.indexOf('?path=')) : '';
            // Use the last URL that the LAN poll PROVED works (no redundant health check needed)
            let baseUrl = lastWorkingPcUrlRef.current || '';
            if (!baseUrl) {
              try { baseUrl = await getCachedPcUrl() || ''; } catch {}
            }
            if (!baseUrl) {
              const pcDev = activeDevicesRef.current.find((d: any) => d.DeviceType === 'PC');
              if (pcDev) {
                if (pcDev._lanVerified && pcDev._lanUrl) baseUrl = pcDev._lanUrl;
                else if (pcDev.GlobalUrl) baseUrl = pcDev.GlobalUrl.replace(/\/$/, '');
              }
            }
            if (baseUrl && pathPart) {
              downloadUrl = `${baseUrl}/download${pathPart}`;
              downloadSource = baseUrl.includes('trycloudflare.com') ? 'Cloud' : 'LAN';
            }
            // If no base URL available, try the original URL as-is (last resort)
            if (!downloadUrl && sourceUrl.startsWith('http')) {
              downloadUrl = sourceUrl;
            }
          }

          if (!downloadUrl) {
            syncLog('IMG-DL', `✗ ${imgItem.Title}: no download URL resolved (raw=${rawUrl.substring(0,30)} src=${sourceUrl.substring(0,30)} dl=${dlUrl.substring(0,30)})`);
            downloadingRef.current.delete(imgItem.id!);
            continue;
          }

          syncLog('IMG-DL', `${imgItem.Title} → ${downloadSource}: ${downloadUrl.substring(0, 80)}`);
          const dlHeaders: Record<string, string> = { 'X-FlyShelf-Client': 'MobileCompanion' };
          if (pairingKeyRef.current) dlHeaders['X-Pairing-Key'] = pairingKeyRef.current;

          // Download with 30s timeout + 1 retry with URL re-resolution
          let dlAttempt = 0;
          let dlSuccess = false;
          while (dlAttempt < 2 && !dlSuccess) {
            try {
              // Re-resolve URL on retry (tunnel URL may have changed)
              if (dlAttempt > 0 && downloadUrl.includes('trycloudflare.com')) {
                try {
                  const freshBase = await getCachedPcUrl();
                  if (freshBase && downloadUrl.includes('?path=')) {
                    const pathPart = downloadUrl.substring(downloadUrl.indexOf('?path='));
                    downloadUrl = `${freshBase}/download${pathPart}`;
                  } else if (freshBase) {
                    downloadUrl = freshBase;
                  }
                } catch (e) { syncLog('IMG-DL', `URL re-resolution failed: ${(e as any)?.message || e}`); }
                syncLog('IMG-DL', `Retry #${dlAttempt}: ${downloadUrl.substring(0, 80)}`);
              }
              const dlResult = await Promise.race([
                FileSystem.downloadAsync(downloadUrl, localUri, { headers: dlHeaders }),
                new Promise<never>((_, reject) => setTimeout(() => reject(new Error('Download timeout (30s)')), 30000)),
              ]);
              const { uri, status } = dlResult as { uri: string; status: number };

              if (status === 200) {
                const info = await FileSystem.getInfoAsync(uri);
                if (info.exists && (info as any).size > 100) {
                  setClips(prev => prev.map(c =>
                    c.id === imgItem.id ? { ...c, Raw: uri, CachedUri: uri, _needsDownload: undefined } : c
                  ));
                  syncLog('IMG-DL', `✓ ${imgItem.Title} via ${downloadSource}`);
                  if (imgItem === needsDownload[0]) {
                    try {
                      const b64 = await FileSystem.readAsStringAsync(uri, { encoding: FileSystem.EncodingType.Base64 });
                      await Clipboard.setImageAsync(b64);
                    } catch (e) { console.warn('Clipboard setImage after download: error', (e as any)?.message || e); }
                  }
                  if (AdvanceOverlay && isFloatingBallEnabled) {
                    try { AdvanceOverlay.pushClipToNativeDB(uri, imgItem.SourceDeviceName || 'PC'); } catch (e) { console.warn('Overlay pushClip after download: error', (e as any)?.message || e); }
                  }
                  if (Platform.OS === 'android') ToastAndroid.show(`🖼️ Screenshot synced from PC!`, ToastAndroid.SHORT);
                }
                dlSuccess = true;
              } else {
                throw new Error(`HTTP ${status}`);
              }
            } catch (dlErr: any) {
              dlAttempt++;
              if (dlAttempt >= 2) {
                syncLog('IMG-DL', `✗ ${imgItem.Title} after ${dlAttempt} attempts: ${dlErr?.message || dlErr}`);
                try { await FileSystem.deleteAsync(localUri, { idempotent: true }); } catch {}
              }
            }
          }
        } catch (err: any) {
          syncLog('IMG-DL', `✗ ${imgItem.Title}: ${err?.message || err}`);
        }
        downloadingRef.current.delete(imgItem.id!);
      }
    })();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [imageDownloadTrigger]);

  // ─── Local PC Polling ───
  const pollLockRef = useRef(false); // Prevents concurrent pollFn from timer + long-poll
  useEffect(() => {
    const pollFn = async () => {
      if (pollLockRef.current) return; // Already running — skip this invocation
      pollLockRef.current = true;
      try {
      // Gate: check if any paired device has clipboard sync enabled
      const anySyncEnabled = pairedDevices.length === 0 || pairedDevices.some(d => getSyncPrefsForDevice(d.deviceId).clipboard);
      if (!anySyncEnabled) { pollLockRef.current = false; return; }
      const targetUrl = await getCachedPcUrl().catch(() => '');
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
          // H-3: Connection quality indicator
          const pollLatency = Math.round(performance.now() - pollStart);
          setConnectionInfo({ url: targetUrl, latencyMs: pollLatency, type: targetUrl.includes('trycloudflare.com') ? 'Cloud' : 'LAN' });
          // Update paired device status with connection type & latency
          const connType = targetUrl.includes('trycloudflare.com') ? 'Cloud' as const : 'LAN' as const;
          pairedDevices.filter(d => d.deviceType === 'PC').forEach(d => {
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
            // ═══ GUARD: Skip items from BEFORE this device paired ═══
            if (pairingTimestampRef.current > 0 && latest.Timestamp && latest.Timestamp < (pairingTimestampRef.current - 5000)) {
              return; // Pre-pairing item — don't sync to Android
            }
            const contentKey = `${latest.Type}_${latest.Title}_${latest.Timestamp}`;
            if (contentKey !== lastSyncedContentRef.current) {
              lastSyncedContentRef.current = contentKey;
              // EventId dedup for LAN poll
              const lanEventId = latest.EventId || '';
              if (lanEventId && processedEventsRef.current.has(lanEventId)) return;
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
                      // Dup found — remove from old position, put at top
                      const updated = [...prev];
                      const existing = updated.splice(dupIdx, 1)[0];
                      scrollToTop();
                      return [{ ...existing, ...resolvedItem, _needsDownload: !existing.CachedUri, Timestamp: NetworkClock.now() }, ...updated];
                    }
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
                      // Dup found — remove from old position, put at top with fresh timestamp
                      const updated = [...prev];
                      const existing = updated.splice(dupIdx, 1)[0];
                      scrollToTop();
                      return [{ ...existing, Raw: existing.Title || existing.Raw, Timestamp: NetworkClock.now() }, ...updated];
                    }
                    scrollToTop();
                    return [{ ...latest, Raw: latest.Title || latest.Raw, Timestamp: NetworkClock.now(), _receivedVia: 'LAN' as const } as any, ...prev];
                  });
                  // If this file was previously deleted by the user, un-delete it so it reappears
                  if (latest.id && localDeletedIds.has(latest.id)) {
                    setLocalDeletedIds(prev => {
                      const n = new Set(prev);
                      n.delete(latest.id);
                      AsyncStorage.getItem('localDeletedIds').then(val => {
                        try {
                          const ids: string[] = val ? JSON.parse(val) : [];
                          AsyncStorage.setItem('localDeletedIds', JSON.stringify(ids.filter(id => id !== latest.id))).catch(() => {});
                        } catch {}
                      });
                      return n;
                    });
                  }
                }
                if (Platform.OS === 'android' && AdvanceOverlay && isFloatingBallEnabled) {
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
                // Dup found — remove from old position, put at top
                const existing = merged.splice(dupIdx, 1)[0];
                merged.unshift({ ...existing, Timestamp: NetworkClock.now() });
                changed = true;
              } else {
                // New — add to top
                merged.unshift({ ...localItem, _receivedVia: 'Cloud' });
                changed = true;
              }
            });
            if (!changed) return current;
            scrollToTop();
            return merged;
          });
          // Trigger background download effects for new clips from LAN poll
          setImageDownloadTrigger(t => t + 1);
          setRichMediaDownloadTrigger(t => t + 1);
        }
      } catch (e) {
        syncLog('PC-POLL', `Poll failed: ${(e as any)?.message || e}`);
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
    let longPollActive = true;
    let currentLongPollController: AbortController | null = null;
    let longPollBackoff = 0;
    const runLongPoll = async () => {
      // Wait for first successful poll to establish cachedPcUrlRef
      await new Promise(r => setTimeout(r, 3000));
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

  // ─── Device Self-Registration ───
  useEffect(() => {
    if (!deviceName) return;
    const myDeviceId = `Mobile_${deviceName.replace(/[^a-zA-Z0-9_]/g, '_')}`;
    const pk = pairingKeyRef.current;
    if (!pk) return;
    const registerSelf = async () => {
      try { await set(ref(database, `active_devices/${pk}/${myDeviceId}`), { DeviceId: myDeviceId, DeviceName: deviceName, DeviceType: 'Mobile', IsOnline: true, LocalIp: '', Timestamp: NetworkClock.now() }); } catch(e) { syncLog('HEARTBEAT', `Device registration failed: ${(e as any)?.message || e}`); }
    };
    registerSelf();
    // Reduced from 30s to 600s — Firebase writes are expensive at scale (10-minute heartbeat)
    const heartbeat = setInterval(registerSelf, 600_000);
    return () => { clearInterval(heartbeat); if (!isFloatingBallEnabled) set(ref(database, `active_devices/${pk}/${myDeviceId}/IsOnline`), false).catch(() => {}); };
  }, [deviceName, isFloatingBallEnabled]);


  // ─── Periodic dedup cleanup (every 60s) ───
  useEffect(() => {
    const cleanup = setInterval(() => {
      const now = NetworkClock.now();
      // processedEventsRef cleanup handled by dedicated interval at L106-114 (5min/10min TTL)
      // Clean sentContentFingerprintsRef — TTL eviction, NOT full clear (prevents echo loops)
      // Legacy fingerprints don't have timestamps, so cap by size instead
      if (sentContentFingerprintsRef.current.size > 500) {
        const arr = Array.from(sentContentFingerprintsRef.current);
        sentContentFingerprintsRef.current = new Set(arr.slice(-200));
        syncLog('CLEANUP', 'Trimmed sentContentFingerprints to 200 (was >500)');
      }
      // Clean recentSyncFingerprintsRef — remove entries older than 60s
      recentSyncFingerprintsRef.current.forEach((ts, fp) => {
        if (now - ts > 60_000) recentSyncFingerprintsRef.current.delete(fp);
      });
    }, 60_000);
    return () => clearInterval(cleanup);
  }, []);

  // ─── Clear All ───
  const clearAllClips = async () => {
    const executeWipe = async () => {
      try {
        const now = NetworkClock.now();
        setLocalWipeTimestamp(now);
        AsyncStorage.setItem('localWipeTimestamp', now.toString()).catch(() => {});
        if (isGlobalSyncEnabled) {
          const updates: any = {};
          clips.forEach(item => { if (!item.IsPinned) updates[item.id!] = null; });
          if (Object.keys(updates).length > 0 && pairingKeyRef.current) await update(ref(database, clipboardPath()), updates);
        }
        Platform.OS === 'android' ? ToastAndroid.show(`Clean slate natively.`, ToastAndroid.SHORT) : alert(`Wiped visually & globally.`);
      } catch(e: any) {
        syncLog('WIPE', `clearAllClips Firebase failed: ${e?.message || e}`);
      }
    };
    if (Platform.OS === 'web') { if (window.confirm("Delete all unpinned items?")) await executeWipe(); return; }
    Alert.alert("Clear Entire Clipboard", "Delete all unpinned items from the Global Mesh?", [{ text: "Cancel", style: "cancel" }, { text: "Delete All", style: "destructive", onPress: executeWipe }]);
  };

  // ─── Clipboard & Media Foreground Checks ───
  const lastCopiedRef = React.useRef(lastCopiedText);
  useEffect(() => { lastCopiedRef.current = lastCopiedText; }, [lastCopiedText]);

  const handleForegroundClipboardCheck = async () => {
    if (Platform.OS === 'web') return;
    try {
      const hasText = await Clipboard.hasStringAsync();
      if (hasText) {
        const text = await Clipboard.getStringAsync();
        // NEVER send flyshelf:// scheme strings — these are internal markers
        if (text && text.startsWith('flyshelf://')) return;
        const normText = normalizeTextForFingerprint(text);
        const normLastCopied = normalizeTextForFingerprint(lastCopiedRef.current || '');
        if (normText && normText !== normLastCopied) {
          lastCopiedRef.current = text; // Set BEFORE transmit to prevent re-entry
          setLastCopiedText(text);
          await transmitTextSecurely(text);
        }
      }
    } catch(e: any) {
      syncLog('CLIPBOARD', `Foreground check failed: ${e?.message || e}`);
    }
  };

  // ─── Screenshot Poller: polls native ScreenshotObserver for new screenshots ───
  const lastSyncedScreenshotRef = useRef<string>('');
  const pollAndSyncScreenshot = async () => {
    if (Platform.OS !== 'android' || !AdvanceOverlay) return;
    try {
      const result = await AdvanceOverlay.getLatestScreenshot();
      const screenshotPath = typeof result === 'string' ? result : result?.path;
      if (screenshotPath && screenshotPath !== lastSyncedScreenshotRef.current) {
        // Check if handleForegroundMediaCheck already handled this
        const fileName = screenshotPath.split('/').pop() || '';
        if (sentContentFingerprintsRef.current.has(`screenshot::${fileName}`)) {
          syncLog('SCREENSHOT', `Already sent by MediaCheck: ${fileName}`);
          lastSyncedScreenshotRef.current = screenshotPath;
          return;
        }
        // Add fingerprint IMMEDIATELY to prevent race with handleForegroundMediaCheck
        sentContentFingerprintsRef.current.add(`screenshot::${fileName}`);
        lastSyncedScreenshotRef.current = screenshotPath;
        syncLog('SCREENSHOT', `Native detected: ${fileName}`);
        const activePc = activeDevices.find((d: any) => d.DeviceType === 'PC');
        let targetUrl = activePc ? ((activePc._lanVerified && activePc._lanUrl) ? activePc._lanUrl : (await resolveOptimalUrl(activePc))) : await getCachedPcUrl();
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
        if (targetUrl) {
          const uploadUri = screenshotPath.startsWith('file://') ? screenshotPath : `file://${screenshotPath}`;
          try {
            const upRes = await FileSystem.uploadAsync(
              `${targetUrl}/api/sync_file?name=${encodeURIComponent(fileName)}&type=Image&sourceDevice=${encodeURIComponent(deviceName || 'Mobile')}`,
              uploadUri,
              { httpMethod: 'POST', uploadType: 0 as any, headers: { 'X-Original-Date': NetworkClock.now().toString(), 'X-FlyShelf-Client': 'MobileCompanion', ...(pairingKeyRef.current ? { 'X-Pairing-Key': pairingKeyRef.current } : {}) } }
            );
            if (upRes.status === 200) {
              syncLog('SCREENSHOT', `Sent to PC via ${targetUrl.includes('trycloudflare') ? 'Cloud' : 'LAN'}: ${fileName}`);
              if (Platform.OS === 'android') ToastAndroid.show(`Screenshot synced to PC ✨`, ToastAndroid.SHORT);
            }
          } catch (e: any) { syncLog('SCREENSHOT', `Upload failed: ${e?.message}`); }
        } else {
          syncLog('SCREENSHOT', `No PC URL available`);
        }
      }
    } catch (e) { console.warn('pollAndSyncScreenshot: error', (e as any)?.message || e); }
  };

  const lastProcessedScreenshotRef = useRef<string>('');
  const mediaPermGrantedRef = useRef<boolean>(false);
  const handleForegroundMediaCheck = async () => {
    try {
      if (!mediaPermGrantedRef.current) {
        let perm = await MediaLibrary.getPermissionsAsync();
        if (perm.status !== 'granted') { perm = await MediaLibrary.requestPermissionsAsync(); if (perm.status !== 'granted') return; }
        mediaPermGrantedRef.current = true;
      }
      const media = await MediaLibrary.getAssetsAsync({ first: 1, mediaType: ['photo'], sortBy: [[MediaLibrary.SortBy.creationTime, false]] });
      if (media.assets.length > 0) {
        const latest = media.assets[0];
        const isRecent = (NetworkClock.now() - latest.creationTime) < 2 * 60 * 1000;
        // ONLY detect screenshots — skip random photos/downloads
        const isScreenshot = (latest.filename || '').toLowerCase().includes('screenshot');
        if (isRecent && isScreenshot && latest.id !== lastScannedImageId) {
          // Ref-based dedup: prevents triple-fire from concurrent interval/AppState/MediaLibrary triggers
          if (lastProcessedScreenshotRef.current === latest.id) return;
          lastProcessedScreenshotRef.current = latest.id;
          // Check if pollAndSyncScreenshot already handled this
          const fp = `screenshot::${latest.filename}`;
          if (sentContentFingerprintsRef.current.has(fp)) {
            syncLog('MEDIA', `Already sent by NativePoll: ${latest.filename}`);
            setLastScannedImageId(latest.id);
            return;
          }
          setLastScannedImageId(latest.id);
          // Add fingerprint IMMEDIATELY to prevent race with pollAndSyncScreenshot
          sentContentFingerprintsRef.current.add(fp);
          setIsSending(true);
          syncLog('MEDIA', `Screenshot detected: ${latest.filename}`);
          try {
            const assetInfo = await MediaLibrary.getAssetInfoAsync(latest.id);
            const assetUri = assetInfo.localUri || assetInfo.uri;
            if (assetUri) {
              // Step 1: Create local cached copy for preview
              const safeName = (assetInfo.filename || `ss_${NetworkClock.now()}.png`).replace(/[^a-zA-Z0-9.-]/g, '_');
              await FileSystem.makeDirectoryAsync(IMAGE_CACHE_BASE, { intermediates: true }).catch(() => {});
              const localCopy = `${IMAGE_CACHE_BASE}${safeName}`;
              try { await FileSystem.copyAsync({ from: assetUri, to: localCopy }); } catch { /* use asset URI directly */ }
              const previewUri = localCopy;

              // Step 2: Create local clip entry (visible immediately in feed)
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
              // Store in ref so Firebase listener can merge it
              localScreenshotsRef.current = [screenshotItem, ...localScreenshotsRef.current].slice(0, 10);
              setClips(prev => [screenshotItem, ...prev.filter(c => c.Title !== screenshotItem.Title)]);
              scrollToTop();
              syncLog('MEDIA', `Local preview created: ${safeName}`);
              // Push to floating ball overlay with image type info
              if (Platform.OS === 'android' && AdvanceOverlay && isFloatingBallEnabled) {
                try { AdvanceOverlay.pushClipToNativeDB(previewUri, deviceName || 'Phone'); } catch {}
              }

              // Step 3: Copy to clipboard
              try {
                const base64 = await FileSystem.readAsStringAsync(previewUri, { encoding: FileSystem.EncodingType.Base64 });
                await Clipboard.setImageAsync(base64);
              } catch {}

              // Step 4: Upload to PC — ONLY if native AdvanceOverlay is NOT available
              // When native is available, pollAndSyncScreenshot handles the upload
              if (!AdvanceOverlay) {
                let targetUrl = await getCachedPcUrl();
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
                let localSuccess = false;
                if (targetUrl) {
                  try {
                    const upRes = await FileSystem.uploadAsync(`${targetUrl}/api/sync_file?name=${encodeURIComponent(assetInfo.filename || 'screenshot.jpg')}&type=ImageLink&sourceDevice=${encodeURIComponent(deviceName || 'Mobile')}`, assetUri, {
                      httpMethod: 'POST', uploadType: 0 as any,
                      headers: { 'X-Original-Date': NetworkClock.now().toString(), 'X-FlyShelf-Client': 'MobileCompanion', ...(pairingKeyRef.current ? { 'X-Pairing-Key': pairingKeyRef.current } : {}) }
                    });
                    localSuccess = upRes.status === 200;
                    if (localSuccess) {
                      syncLog('MEDIA', `Sent to PC via ${targetUrl.includes('trycloudflare') ? 'Cloud' : 'LAN'}: ${assetInfo.filename}`);
                    }
                  } catch(e: any) { syncLog('MEDIA', `Upload failed: ${e?.message}`); }
                }
                if (!localSuccess) {
                  syncLog('MEDIA', `Could not reach PC`);
                  if (Platform.OS === 'android') ToastAndroid.show(`⚠️ Could not reach PC to send screenshot`, ToastAndroid.SHORT);
                } else {
                  if (Platform.OS === 'android') ToastAndroid.show(`📸 Screenshot sent to PC!`, ToastAndroid.SHORT);
                }
              } else {
                syncLog('MEDIA', `Upload delegated to native SCREENSHOT handler`);
              }
            }
          } catch(e: any) {
            syncLog('MEDIA', `Media check failed: ${e?.message || e}`);
          }
          setIsSending(false);
        }
      }
    } catch(e: any) {
      syncLog('MEDIA', `Media outer check failed: ${e?.message || e}`);
    }
  };

  useEffect(() => {
    // Always run clipboard + media checks (don't skip when floating ball is on)
    handleForegroundClipboardCheck();
    handleForegroundMediaCheck();
    const subscription = AppState.addEventListener('change', (nextAppState: AppStateStatus) => {
      if (nextAppState === 'active') { handleForegroundClipboardCheck(); handleForegroundMediaCheck(); }
    });
    // Poll for new media every 3 seconds
    let screenshotPollInterval: ReturnType<typeof setInterval> | null = null;
    if (Platform.OS !== 'web') { screenshotPollInterval = setInterval(() => handleForegroundMediaCheck(), 3000); }
    // Poll native ScreenshotObserver every 2 seconds
    let nativeScreenshotPoll: ReturnType<typeof setInterval> | null = null;
    if (Platform.OS === 'android') { nativeScreenshotPoll = setInterval(() => pollAndSyncScreenshot(), 2000); }
    let mediaSub: any = null;
    if (Platform.OS !== 'web' && typeof MediaLibrary.addListener === 'function') {
      mediaSub = MediaLibrary.addListener((event) => { if (event.hasIncrementalChanges || (event as any).insertedMedia?.length > 0) handleForegroundMediaCheck(); });
    }
    return () => { subscription.remove(); if (mediaSub) mediaSub.remove(); if (screenshotPollInterval) clearInterval(screenshotPollInterval); if (nativeScreenshotPoll) clearInterval(nativeScreenshotPoll); };
  }, [deviceName, isGlobalSyncEnabled, activeDevices]);

  // ─── WiFi Switch Auto-Recovery (TASK 1A) ───
  useEffect(() => {
    const netInfoUnsubscribe = NetInfo.addEventListener(state => {
      if (state.isConnected && state.type === 'wifi') {
        syncLog('NET', 'WiFi state changed — invalidating cache for fresh discovery');
        invalidatePcUrlCache();
        lastWorkingPcUrlRef.current = null;
        // Trigger immediate re-poll by invalidating cached URL (next poll cycle picks up fresh)
        cachedPcUrlRef.current = null;
      } else if (!state.isConnected) {
        setConnectionInfo({ url: '', latencyMs: 0, type: 'LAN' });
      }
    });
    return () => { netInfoUnsubscribe(); };
  }, [invalidatePcUrlCache]);

  // ─── Auto-Copy Incoming ───
  useEffect(() => {
    if (clips.length === 0) return;
    const latest = clips[0];
    if (latest.id !== latestIngestedId) {
      setLatestIngestedId(latest.id || latest.Title || 'ts_' + latest.Timestamp);
      if (latest.SourceDeviceName !== deviceName) {
        if (Platform.OS === 'web') return;
        (async () => {
          try {
            if (latest.Type === 'Text' || latest.Type === 'Url' || latest.Type === 'Code') {
              const currentClip = await Clipboard.getStringAsync();
              const normCurrent = normalizeTextForFingerprint(currentClip);
              const normLatest = normalizeTextForFingerprint(latest.Raw || '');
              if (normCurrent !== normLatest) { await Clipboard.setStringAsync(latest.Raw); setLastCopiedText(latest.Raw); lastCopiedRef.current = latest.Raw; Platform.OS === 'android' && ToastAndroid.show("Copied Natively", ToastAndroid.SHORT); }
            } else if (latest.Type === 'Image' || latest.Type === 'ImageLink') {
              const mediaUrl = getMediaUrlForItem(latest);
              if (mediaUrl) {
                const { uri } = await FileSystem.downloadAsync(mediaUrl, SYNC_CACHE_BASE + 'clip_sync_global.png', {
                  headers: {
                    'X-FlyShelf-Client': 'MobileCompanion',
                    'X-Pairing-Key': pairingKeyRef.current || ''
                  }
                });
                const b64 = await FileSystem.readAsStringAsync(uri, { encoding: (FileSystem as any).EncodingType.Base64 });
                await Clipboard.setImageAsync(b64);
                Platform.OS === 'android' && ToastAndroid.show("Image Copied Natively", ToastAndroid.SHORT);
              }
            }
          } catch (e) { syncLog('SYNC', `Auto-copy failed: ${(e as any)?.message || e}`); }
        })();
      }
    }
  }, [clips, deviceName, latestIngestedId]);

  // ─── Auto-Download Rich Media ───
  useEffect(() => {
    // Use ref to read clips without depending on clips (prevents infinite loop)
    const currentClips = clipsStateRef.current;
    if (currentClips.length === 0) return;
    let aborted = false;
    // I-4 fix: track in-flight download resumables so cleanup can cancel them.
    // The aborted flag alone only prevented NEW downloads from starting -
    // in-progress downloads kept running as zombies after unmount/re-run.
    const activeResumables = new Set<any>();
    // M-9: Process downloads in batches of 3 to limit concurrency
    const downloadBatch = async () => {
      const BATCH_SIZE = 3;
      for (let i = 0; i < currentClips.length; i += BATCH_SIZE) {
        if (aborted) return;
        const batch = currentClips.slice(i, i + BATCH_SIZE);
        await Promise.all(batch.map(async (item) => {
          if (aborted) return;
          if (!item.id || downloadedItems.has(item.id)) return;
          const autoTargetTypes = ['ImageLink', 'Image', 'Pdf', 'Document', 'Archive', 'Video', 'File', 'Presentation'];
          const mediaUrl = getMediaUrlForItem(item);
          // C-2: Prefer LAN URL over cloud for downloads
          let finalMediaUrl = mediaUrl;
          if (lastWorkingPcUrlRef.current && mediaUrl.includes('firebasestorage.googleapis.com')) {
            // Rewrite to use LAN if PC is reachable
            const relPath = item.DownloadUrl || item.PreviewUrl || '';
            if (relPath.startsWith('/')) {
              finalMediaUrl = `${lastWorkingPcUrlRef.current}${relPath}`;
            }
          }
          if (autoTargetTypes.includes(item.Type) && finalMediaUrl.startsWith('http')) {
            try {
              if (Platform.OS === 'web') return;
              const safeName = item.Title.replace(/[^a-zA-Z0-9.-]/g, '_');
              const localUri = DOWNLOAD_BASE + safeName;
              const transferId = item.id || safeName;
              const fileInfo = await FileSystem.getInfoAsync(localUri);
              if (fileInfo.exists) { setDownloadedItems(prev => new Set(prev).add(item.id!)); setIncomingTransferProgress(p => { const n = {...p}; delete n[transferId]; return n; }); return; }
              if ((item.Title || '').toLowerCase().endsWith('.apk')) return;
              try {
                // H-5: Add timeout to HEAD request to prevent hangs
                const headRes = await fetchWithTimeout(finalMediaUrl, { 
                  method: 'HEAD', 
                  headers: { 
                    'X-FlyShelf-Client': 'MobileCompanion',
                    'X-Pairing-Key': pairingKeyRef.current || ''
                  } 
                }, 3000);
                const sizeStr = headRes.headers.get('content-length');
                if (sizeStr) { const sizeBytes = parseInt(sizeStr); const isLocalRoute = !finalMediaUrl.includes('firebasestorage.googleapis.com'); if (!isLocalRoute && sizeBytes > 100 * 1024 * 1024) return; }
              } catch(e: any) { console.warn('Auto-download HEAD check: error', e?.message || e); }
              setIncomingTransferProgress(p => ({...p, [transferId]: 0}));
              const resumable = FileSystem.createDownloadResumable(
                finalMediaUrl, 
                localUri, 
                { 
                  headers: { 
                    'X-FlyShelf-Client': 'MobileCompanion',
                    'X-Pairing-Key': pairingKeyRef.current || ''
                  } 
                }, 
                (dp) => { const pct = dp.totalBytesExpectedToWrite > 0 ? dp.totalBytesWritten / dp.totalBytesExpectedToWrite : 0; setIncomingTransferProgress(p => ({...p, [transferId]: pct})); }
              );
              activeResumables.add(resumable);
              let dlResult;
              try { dlResult = await resumable.downloadAsync(); }
              finally { activeResumables.delete(resumable); }
              setIncomingTransferProgress(p => { const n = {...p}; delete n[transferId]; return n; });
              if (dlResult && dlResult.status === 200) {
                setDownloadedItems(prev => new Set(prev).add(item.id!));
                if (item.Type === 'ImageLink' || item.Type === 'Image') { try { const perm = await MediaLibrary.requestPermissionsAsync(); if (perm.status === 'granted') await MediaLibrary.saveToLibraryAsync(localUri); } catch (err: any) { console.warn('Auto-download save to library: error', err?.message || err); } }
                // Track download via downloadedBy model
                if (item.id) { try { await markFileDownloaded(item.id); } catch (err: any) { console.warn('Auto-download mark downloaded: error', err?.message || err); } }
              } else {
                // Failed — delete partial/corrupt file (NEVER resume)
                await FileSystem.deleteAsync(localUri, { idempotent: true }).catch(() => {});
              }
            } catch(e) { const transferId = item.id || (item.Title || '').replace(/[^a-zA-Z0-9.-]/g, '_'); setIncomingTransferProgress(p => { const n = {...p}; delete n[transferId]; return n; }); await FileSystem.deleteAsync(DOWNLOAD_BASE + (item.Title || '').replace(/[^a-zA-Z0-9.-]/g, '_'), { idempotent: true }).catch(() => {}); }
          } else { setDownloadedItems(prev => new Set(prev).add(item.id!)); }
        }));
      }
    };
    downloadBatch();
    return () => {
      aborted = true;
      // I-4 fix: actively cancel in-flight downloads, not just future ones
      activeResumables.forEach(r => { try { r.cancelAsync().catch(() => {}); } catch {} });
      activeResumables.clear();
    };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [richMediaDownloadTrigger]);

  // ─── Send Text ───
  const transmitTextSecurely = async (payloadText: string) => {
    // I-6 fix: read the freshest clips via ref - the closure-captured `clips`
    // can be stale when this is called from AppState/interval callbacks.
    const isDuplicate = clipsStateRef.current.some(c => c.Raw === payloadText || c.Title === payloadText);
    if (isDuplicate) return;
    setIsSending(true);
    try {
      let finalRaw = payloadText, finalType = 'Text';
      if (payloadText.startsWith('http')) finalType = 'Url';
      else if (payloadText.includes('meet.google.com') || payloadText.includes('zoom.us') || payloadText.startsWith('www.')) { finalType = 'Url'; finalRaw = `https://${payloadText}`; }
      let targetUrl = await getCachedPcUrl();
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
      sentContentFingerprintsRef.current.add(finalRaw.substring(0, 200));
      const txEventId = generateEventId();
      processedEventsRef.current.set(txEventId, NetworkClock.now());
      let localSuccess = false;
      try {
        const pairingKey = await getSecureItem('pairingKey');
        const hdrs: any = { 'Content-Type': 'application/json', 'X-FlyShelf-Client': 'MobileCompanion', 'X-Source-Device': deviceName || 'Mobile' };
        if (pairingKey) hdrs['X-Pairing-Key'] = pairingKey;
        const jsonBody = JSON.stringify({
          type: finalType,
          title: payloadText.length > 40 ? payloadText.substring(0, 40) + '...' : payloadText,
          data: finalRaw,
          sourceDeviceName: deviceName || 'Mobile',
          sourceDeviceId: `Mobile_${(deviceName || 'Phone').replace(/[^a-zA-Z0-9_]/g, '_')}`,
          timestamp: NetworkClock.now(),
        });
        const sendTimeout = targetUrl.includes('trycloudflare.com') ? 8000 : 3000;
        const response = await fetchWithTimeout(`${targetUrl}/api/sync_text`, { method: 'POST', headers: hdrs, body: jsonBody }, sendTimeout);
        localSuccess = response.ok;
        if (localSuccess && Platform.OS === 'android') ToastAndroid.show('✓ Text sent', ToastAndroid.SHORT);
      } catch(e) { syncLog('SYNC', `Text transmit to PC failed: ${(e as any)?.message || e}`); cachedPcUrlRef.current = null; if (Platform.OS === 'android') ToastAndroid.show('✗ Text send failed — queued for cloud', ToastAndroid.SHORT); }
      // Always add sent text to local clips so it appears in the feed
      const sentItem: ClipItem = {
        id: `local_${NetworkClock.now()}`,
        Title: payloadText.length > 50 ? payloadText.substring(0, 50) + '...' : payloadText,
        Type: finalType,
        Raw: finalRaw,
        Time: new Date().toLocaleTimeString(),
        Timestamp: NetworkClock.now(),
        SourceDeviceName: deviceName || 'Mobile',
        SourceDeviceType: 'Mobile',
        _receivedVia: 'Local',
      };
      setClips(prev => {
        const exists = prev.some(c => c.Raw === finalRaw);
        if (exists) return prev;
        scrollToTop();
        return [sentItem, ...prev];
      });

      if (!localSuccess && isGlobalSyncEnabled) {
        // Sync Queue: retry with exponential backoff for guaranteed delivery
        // AES-256-GCM encrypt sensitive fields before Firebase push
        let encTitle = payloadText.length > 50 ? payloadText.substring(0, 50) + '...' : payloadText;
        let encRaw = finalRaw;
        let encrypted = false;
        try {
          encTitle = await aesEncrypt(encTitle);
          encRaw = await aesEncrypt(encRaw);
          encrypted = true;
        } catch (e: any) { syncLog('SYNC_CRYPTO', `Encryption failed, sending plaintext: ${e?.message || 'unknown'}`); }
        // Size validation: reject payloads > 1MB to prevent Firebase billing abuse
        if (encRaw.length > 1_000_000) { syncLog('SYNC', 'Payload too large for Firebase (>1MB), skipping cloud sync'); setIsSending(false); return; }
        const payload = { Title: encTitle, Type: finalType, Raw: encRaw, Time: new Date().toLocaleTimeString(), Timestamp: NetworkClock.now(), EventId: txEventId, Encrypted: encrypted, SourceDeviceName: deviceName || 'Unknown Mobile', SourceDeviceType: 'Mobile' };
        const RETRY_DELAYS = [2000, 5000, 10000];
        for (let attempt = 0; attempt < RETRY_DELAYS.length; attempt++) {
          try {
            const newRef = push(ref(database, clipboardPath()));
            await set(newRef, payload);
            if (attempt > 0) syncLog('SYNC_QUEUE', `Delivered to Firebase after ${attempt + 1} attempts`);
            break; // Success
          } catch (err: any) {
            syncLog('SYNC_QUEUE', `Attempt ${attempt + 1}/${RETRY_DELAYS.length} failed: ${err?.message || 'unknown'}`);
            if (attempt < RETRY_DELAYS.length - 1) {
              await new Promise(r => setTimeout(r, RETRY_DELAYS[attempt]));
            }
          }
        }
      }
    } catch (e) { syncLog('SYNC', `Text transmit error: ${(e as any)?.message || e}`); }
    setIsSending(false);
  };

  // ─── Multi-Select ───
  const getSelectedClips = () => clips.filter(c => (c.IsPinned || (c.Timestamp || 0) >= localWipeTimestamp) && (!c.id || !localDeletedIds.has(c.id)) && (c.Raw || c.Title)).filter(c => selectedItemIds.has(c.id || ''));

  // ─── PDF Merge ───
  const openMergeModal = () => {
    const selected = getSelectedClips();
    
    // Check if any Word document is selected (.doc, .docx)
    const hasWordDoc = selected.some(c => {
      const title = (c.Title || '').toLowerCase();
      const type = c.Type || '';
      return title.endsWith('.docx') || title.endsWith('.doc') || type === 'Document';
    });

    if (hasWordDoc) {
      Alert.alert(
        'Word Documents Protected',
        'Word documents (.docx) cannot be converted natively on mobile. Please use your paired PC companion app to merge/convert Word documents.'
      );
      return;
    }

    // Filter selection to PDFs and Images
    const mergeableSelected = selected.filter(c => {
      const title = (c.Title || '').toLowerCase();
      const type = c.Type || '';
      return type === 'Pdf' || type === 'Image' || type === 'ImageLink' || 
             title.endsWith('.pdf') || title.endsWith('.png') || title.endsWith('.jpg') || title.endsWith('.jpeg');
    });

    if (mergeableSelected.length < 2) {
      Alert.alert('Merge Requirements', 'Please select at least 2 files (PDFs or Images) to merge.');
      return;
    }

    setMergeQueue([...mergeableSelected]);
    setIsMergeModalVisible(true);
  };
  const moveMergeItem = (fromIdx: number, toIdx: number) => { if (toIdx < 0 || toIdx >= mergeQueue.length) return; setMergeQueue(prev => { const arr = [...prev]; const [moved] = arr.splice(fromIdx, 1); arr.splice(toIdx, 0, moved); return arr; }); };
  const executePdfMerge = async () => {
    try {
      setIsMergeModalVisible(false);
      if (Platform.OS === 'android') ToastAndroid.show('Merging files on device...', ToastAndroid.LONG);

      // Resolve file URIs (local cached or remote URLs)
      const pdfUris = mergeQueue.map(item => {
        const mUrl = getMediaUrlForItem(item);
        // Prefer local cached version
        if (item.CachedUri) return item.CachedUri;
        const safeName = (item.Title || '').replace(/[^a-zA-Z0-9.-]/g, '_');
        const localPath = DOWNLOAD_BASE + safeName;
        return mUrl || localPath;
      }).filter(u => u && u.length > 0);

      if (pdfUris.length < 2) { Alert.alert('Error', 'Could not resolve mergeable files.'); return; }

      const outputPath = CONVERTED_BASE + `merged_${NetworkClock.now()}.pdf`;

      try {
        // Try local merge first (on-device, no PC needed)
        await localMergePdfs(pdfUris, outputPath);
        if (Platform.OS === 'android') ToastAndroid.show('✅ Files merged on device!', ToastAndroid.SHORT);
        await Sharing.shareAsync(outputPath, { mimeType: 'application/pdf', UTI: 'com.adobe.pdf', dialogTitle: 'Merged PDF' });
      } catch (localErr: any) {
        // Fallback: try PC merge
        if (Platform.OS === 'android') ToastAndroid.show('Local merge failed, trying PC...', ToastAndroid.SHORT);
        let targetUrl = '';
        if (pcLocalIp?.trim()) {
          const rawParts = pcLocalIp.split(',').map(s => s.trim()).filter(Boolean);
          if (rawParts.length > 0) {
            const raw = rawParts[0];
            targetUrl = raw.startsWith('http') ? raw.replace(/\/$/, '') : `http://${raw.includes(':') ? raw : raw + ':8999'}`;
          }
        }
        const activePc = activeDevices.find((d: any) => d.DeviceType === 'PC');
        if (activePc) {
          const opt = await resolveOptimalUrl(activePc, fetchWithTimeout, pairingKeyRef.current);
          if (opt) {
            targetUrl = opt;
          } else if (lastWorkingPcUrlRef.current) {
            targetUrl = lastWorkingPcUrlRef.current;
          }
        }
        const pdfUrls = mergeQueue.map(item => getMediaUrlForItem(item)).filter(u => u.startsWith('http'));
        if (pdfUrls.length < 2) { Alert.alert('Error', `Local: ${localErr.message}\nPC: No HTTP URLs available.`); return; }
        const res = await fetchWithTimeout(`${targetUrl}/api/merge_pdfs`, { 
          method: 'POST', 
          headers: { 
            'Content-Type': 'application/json', 
            'X-FlyShelf-Client': 'MobileCompanion',
            'X-Pairing-Key': pairingKeyRef.current || ''
          }, 
          body: JSON.stringify({ urls: pdfUrls, sourceDevice: deviceName || 'Mobile' }) 
        }, 30000);
        if (res.ok) { 
          const body = await res.json(); 
          if (body.downloadUrl) { 
            const mergedUrl = body.downloadUrl.startsWith('http') ? body.downloadUrl : `${targetUrl}${body.downloadUrl}`; 
            const localUri = CONVERTED_BASE + `merged_${NetworkClock.now()}.pdf`; 
            await FileSystem.downloadAsync(mergedUrl, localUri, { 
              headers: { 
                'X-FlyShelf-Client': 'MobileCompanion',
                'X-Pairing-Key': pairingKeyRef.current || ''
              } 
            }); 
            await Sharing.shareAsync(localUri, { mimeType: 'application/pdf', UTI: 'com.adobe.pdf', dialogTitle: 'Merged PDF' }); 
          } 
        } else Alert.alert('Merge Failed');
      }
    } catch (e) { Alert.alert('Merge Error'); }
    exitMultiSelect();
  };

  const handleSanitizeUrl = async (item: ClipItem) => {
    try {
      const original = item.Raw || item.Title || '';
      if (!original) return;
      
      const rxUtmClean = /(?<=&|\?)(utm_source|utm_medium|utm_campaign|utm_term|utm_content|gclid|fbclid|_gl|msclkid|mc_eid|ig_shid)=[^&]*&?/gi;
      const cleanUrl = original.replace(rxUtmClean, '').replace(/[?&]$/, '');
      
      if (cleanUrl !== original) {
        // Copy to system clipboard
        await Clipboard.setStringAsync(cleanUrl);
        
        // Update local state
        setClips(prev => prev.map(c => c.id === item.id ? { ...c, Raw: cleanUrl, Title: cleanUrl } : c));
        
        // Write to Firebase if matched
        if (item.id && !item.id.startsWith('local_') && pairingKeyRef.current) {
          try {
            const encTitle = await aesEncrypt(cleanUrl);
            const encRaw = await aesEncrypt(cleanUrl);
            await update(ref(database, `${clipboardPath()}/${item.id}`), { Title: encTitle, Raw: encRaw });
          } catch (e) {
            await update(ref(database, `${clipboardPath()}/${item.id}`), { Title: cleanUrl, Raw: cleanUrl });
          }
        }
        if (Platform.OS === 'android') ToastAndroid.show("URL Sanitized & Copied! 🛡️", ToastAndroid.SHORT);
      } else {
        if (Platform.OS === 'android') ToastAndroid.show("URL is already clean! ✨", ToastAndroid.SHORT);
      }
    } catch (e) {
      if (Platform.OS === 'android') ToastAndroid.show("Sanitization failed", ToastAndroid.SHORT);
    }
  };

  const handleConvertImageToPdf = async (item: ClipItem) => {
    try {
      if (Platform.OS === 'android') ToastAndroid.show('Converting Image to PDF...', ToastAndroid.SHORT);
      
      const mediaUrl = getMediaUrlForItem(item);
      const imgUri = item.CachedUri || mediaUrl || item.Raw || '';
      if (!imgUri) {
        Alert.alert('Error', 'Image source not found.');
        return;
      }
      
      const safeName = (item.Title || `image_${NetworkClock.now()}.png`).replace(/[^a-zA-Z0-9.-]/g, '_');
      let safeTitleWithoutExt = safeName;
      const lastDotIndex = safeName.lastIndexOf('.');
      if (lastDotIndex > 0) {
        safeTitleWithoutExt = safeName.substring(0, lastDotIndex);
      }
      const pdfFileName = `${safeTitleWithoutExt}_converted_${NetworkClock.now().toString().slice(-4)}.pdf`;
      const pdfPath = DOWNLOAD_BASE + 'PDFs/' + pdfFileName;

      await localConvertImageToPdf(imgUri, pdfPath);
      
      if (Platform.OS === 'android') ToastAndroid.show('✅ Image converted to PDF!', ToastAndroid.SHORT);
      
      // Construct a new PDF ClipItem and insert it at the top of the clips feed
      const newPdfItem: ClipItem = {
        id: `local_pdf_${NetworkClock.now()}`,
        Title: pdfFileName,
        Type: 'Pdf',
        Raw: pdfPath,
        CachedUri: pdfPath,
        Time: new Date().toLocaleTimeString(),
        Timestamp: NetworkClock.now(),
        SourceDeviceName: deviceName || 'Mobile',
        SourceDeviceType: 'Mobile',
        _receivedVia: 'Local',
      };
      
      setClips(prev => {
        scrollToTop();
        return [newPdfItem, ...prev];
      });

      // Show share or open panel
      await Sharing.shareAsync(pdfPath, { mimeType: 'application/pdf', UTI: 'com.adobe.pdf', dialogTitle: 'Converted PDF' });

    } catch (err: any) {
      console.warn(`Conversion error: ${err.message}`);
      Alert.alert('Conversion Failed', err.message || 'Unknown error occurred.');
    }
  };

  // ─── Force Sync ───
  const openForceSyncModal = async () => {
    if (selectedItemIds.size === 0) { Alert.alert('Nothing Selected'); return; }
    // v5: Use cached activeDevices first (URLs may be auto-deleted from Firebase after 5s)
    try {
      if (activeDevicesRef.current.length > 0) {
        const devs = activeDevicesRef.current.filter((d: any) => d.DeviceName !== deviceName);
        setForceSyncDevices(devs.map((d: any) => ({ key: d._key || d.DeviceId, ...d })));
      } else {
        // Fallback: try Firebase (might be empty if URLs were cleaned)
        const pk = pairingKeyRef.current;
        if (!pk) { setForceSyncDevices([]); setIsForceSyncModalVisible(true); return; }
        const { get: firebaseGet } = await import('firebase/database');
        const snapshot = await firebaseGet(ref(database, `active_devices/${pk}`));
        if (snapshot.exists()) {
          const data = snapshot.val();
          const rawDevs = Object.keys(data).map(k => ({ key: k, ...data[k], DeviceId: k }));
          const decryptedDevs = await decryptDeviceList(rawDevs);
          setForceSyncDevices(decryptedDevs.filter(d => d.DeviceName !== deviceName));
        } else setForceSyncDevices([]);
      }
    } catch (e) { setForceSyncDevices([]); }
    setIsForceSyncModalVisible(true);
  };
  const executeForcedSync = async (targetDeviceKeys: string[]) => {
    try {
    setIsForceSyncModalVisible(false);
    const selected = getSelectedClips();
    if (selected.length === 0) { syncLog('FORCE-SYNC', 'No items selected'); return; }
    syncLog('FORCE-SYNC', `Syncing ${selected.length} items to ${targetDeviceKeys.length} devices`);
    if (Platform.OS === 'android') ToastAndroid.show(`Force syncing ${selected.length} items...`, ToastAndroid.LONG);
    try {
      for (const deviceKey of targetDeviceKeys) { for (const item of selected) { const forcedRef = push(ref(database, `forced_sync/${deviceKey}`)); await set(forcedRef, { ...item, ForcedBy: deviceName, ForcedAt: NetworkClock.now(), SourceDeviceName: deviceName, SourceDeviceType: 'Mobile' }); } }
      for (const item of selected) { if (!item.id && pairingKeyRef.current) { const clipRef = push(ref(database, clipboardPath())); await set(clipRef, { ...item, Timestamp: NetworkClock.now() }); } }
      for (const deviceKey of targetDeviceKeys) {
        const dev = forceSyncDevices.find(d => d.key === deviceKey);
        if (dev?.LocalIp) {
          try {
            let url = await resolveOptimalUrl(dev);
            if (!url) {
              const raw = dev.LocalIp.trim();
              url = raw.startsWith('http') ? raw.replace(/\/$/, '') : `http://${raw.includes(':') ? raw : raw + ':8999'}`;
            }
            if (url) {
              for (const item of selected) {
                await fetchWithTimeout(`${url}/api/sync`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-FlyShelf-Client': 'MobileCompanion' }, body: JSON.stringify({ title: item.Title, content: item.Raw, type: item.Type, sourceDevice: deviceName }) }, 5000).catch(() => {});
              }
            }
          } catch (e) { console.warn('Force sync to device: error', (e as any)?.message || e); }
        }
      }
      if (Platform.OS === 'android') ToastAndroid.show('Force sync complete ✅', ToastAndroid.SHORT);
    } catch (e: any) { syncLog('FORCE-SYNC', `ERROR: ${e?.message}`); Alert.alert('Sync Error', e?.message || 'Unknown error'); }
    } catch (outerErr: any) { syncLog('FORCE-SYNC', `CRASH: ${outerErr?.message}`); Alert.alert('Error', outerErr?.message || 'Unexpected error'); }
    exitMultiSelect();
  };

  // ─── File/Camera/QR Actions ───
  const sendTextToPc = async () => { if (!inputText.trim()) return; await transmitTextSecurely(inputText); setInputText(''); };
  const pickFileAndSend = async () => {
    try {
      const result = await DocumentPicker.getDocumentAsync({ type: '*/*' });
      if (result.canceled) return;
      const file = result.assets[0];
      const ext = file.name.split('.').pop()?.toLowerCase() || '';
      let assignedType = 'Document';
      if (['apk','zip','rar'].includes(ext)) assignedType = 'Archive';
      else if (ext === 'pdf') assignedType = 'Pdf';
      else if (['mp4','avi','mkv'].includes(ext)) assignedType = 'Video';
      else if (['ppt','pptx'].includes(ext)) assignedType = 'Presentation';
      else if (['jpg','jpeg','png','gif','webp'].includes(ext)) assignedType = 'Image';
      else if (['doc','docx','txt'].includes(ext)) assignedType = 'Document';
      else assignedType = 'File';
      const payload = { uri: file.uri, name: file.name, size: file.size, type: assignedType };
      setPendingUploadPayload(payload);
      // Auto-send to PC via LAN/Cloudflare if available, skip Firebase
      const pc = activeDevices.find((d: any) => d.DeviceType === 'PC');
      if (pc) {
        executeHeavyUpload(pc, payload);
      } else {
        setIsTargetModalVisible(true);
      }
    } catch (err) { Alert.alert('Upload Failed'); }
  };
  const launchDirectCamera = async () => {
    setIsCameraOptionsVisible(false);
    try {
      const result = await ImagePicker.launchCameraAsync({ mediaTypes: ['images'], allowsEditing: false, quality: 0.8 });
      if (!result.canceled) {
        const file = result.assets[0];
        try { const b64 = await FileSystem.readAsStringAsync(file.uri, { encoding: (FileSystem as any).EncodingType.Base64 }); await Clipboard.setImageAsync(b64); Platform.OS === 'android' ? ToastAndroid.show("Captured & Copied", ToastAndroid.SHORT) : null; } catch (e) {}
        const payload = { uri: file.uri, name: file.fileName || `camera_${NetworkClock.now()}.jpg`, size: file.fileSize, type: 'Image' };
        const pc = activeDevices.find((d: any) => d.DeviceType === 'PC');
        setPendingUploadPayload(payload);
        if (pc) { executeHeavyUpload(pc, payload); } else { setIsTargetModalVisible(true); }
      }
    } catch (camErr: any) {
      Alert.alert('Camera Error', camErr?.message || 'Failed to launch camera');
    }
  };
  const pickImageAndSend = async () => {
    try {
      const result = await ImagePicker.launchImageLibraryAsync({ mediaTypes: ['images', 'videos'], allowsEditing: false, quality: 0.8 });
      if (!result.canceled) {
        const file = result.assets[0];
        try { if (file.type === 'image') { const b64 = await FileSystem.readAsStringAsync(file.uri, { encoding: (FileSystem as any).EncodingType.Base64 }); await Clipboard.setImageAsync(b64); } } catch (e) {}
        const payload = { uri: file.uri, name: file.fileName || `media_${NetworkClock.now()}`, size: file.fileSize, type: file.type === 'video' ? 'Video' : 'Image' };
        const pc = activeDevices.find((d: any) => d.DeviceType === 'PC');
        setPendingUploadPayload(payload);
        if (pc) { executeHeavyUpload(pc, payload); } else { setIsTargetModalVisible(true); }
      }
    } catch (pickErr: any) {
      Alert.alert('Image Picker Error', pickErr?.message || 'Failed to open image library');
    }
  };
  const launchQRScanner = async () => { setIsConnectModalVisible(false); setIsCameraOptionsVisible(false); if (!cameraPermission?.granted) { const perm = await requestCameraPermission(); if (!perm.granted) { Alert.alert("Permission Required"); return; } } setIsQRScannerActive(true); };

  // ─── Pairing System ───
  const executePairing = async (pairInfo: { key?: string; local?: string; global?: string; pin?: string; name?: string; id?: string }) => {
    const { key, local, global: globalUrl, pin, name: pcName, id: pcId } = pairInfo;
    setIsPairing(true);
    if (Platform.OS === 'android') ToastAndroid.show(`Connecting to ${pcName || 'device'}...`, ToastAndroid.SHORT);

    const urls = [local, globalUrl].filter(u => u && u.startsWith('http')) as string[];
    let paired = false, workingUrl = '';
    let pairedPcIsPro = false;
    let pairedPcLicenseKey = '';

    for (const url of urls) {
      try {
        const res = await fetchWithTimeout(`${url}/api/pair`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'X-FlyShelf-Client': 'MobileCompanion' },
          body: JSON.stringify({
            key: key || '',
            deviceId: `Mobile_${(deviceName || 'Phone').replace(/[^a-zA-Z0-9_]/g, '_')}`,
            deviceName: deviceName || 'Phone',
            deviceType: 'Mobile',
          }),
        }, 6000);
        if (res.ok) {
          try {
            const data = await res.json();
            pairedPcIsPro = !!data.isPro;
            pairedPcLicenseKey = data.licenseKey || '';
          } catch (e) { console.warn('Pairing response parse: error', (e as any)?.message || e); }
          paired = true;
          workingUrl = url;
          break;
        }
      } catch (e) { console.warn('Pairing fetch: error', (e as any)?.message || e); }
    }

    // ═══ ALWAYS save pairing info — the key is what matters for cloud sync ═══
    // Even if we can't reach the PC right now, the shared key enables Firebase sync.
    const pairingTs = NetworkClock.now().toString();
    await Promise.all([
      setSecureItem('pairingKey', key || ''),
      setSecureItem('pairedPcName', pcName || ''),
      setSecureItem('pairedLocalUrl', local || ''),
      setSecureItem('pairedGlobalUrl', globalUrl || ''),
      AsyncStorage.multiSet([
        ['pairedPcId', pcId || ''],
        ['pairedPin', pin || ''],
        ['pairingTimestamp', pairingTs]
      ])
    ]);
    pairingKeyRef.current = key || '';
    if (Platform.OS === 'android' && AdvanceOverlay?.setPairingKey && key) AdvanceOverlay.setPairingKey(key);
    pairingTimestampRef.current = parseInt(pairingTs);
    if (workingUrl) {
      cachedPcUrlRef.current = workingUrl;
      cachedPcUrlTimestampRef.current = NetworkClock.now();
    }
    setPairedPcName(pcName || 'Device');
    if (!isGlobalSyncEnabled) setGlobalSyncEnabled(true);

    // Register the remote device in the paired devices list
    const deviceType = (pairInfo as any).deviceType || 'PC';
    await addPairedDevice({
      deviceId: pcId || `${pcName}_${NetworkClock.now()}`,
      deviceName: pcName || 'Unknown Device',
      deviceType: deviceType as 'PC' | 'Mobile' | 'Browser',
      pairedAt: NetworkClock.now(),
      isPro: pairedPcIsPro,
      licenseKey: pairedPcLicenseKey,
    });

    setIsPairing(false);

    if (paired) {
      if (Platform.OS === 'android') ToastAndroid.show(`✅ Paired with ${pcName}!`, ToastAndroid.LONG);
      Alert.alert('Connected! 🎉',
        `Paired with ${pcName}.\n\nAnything you copy or drop on your PC will appear here instantly — from anywhere in the world.`,
        [{ text: 'Got it!' }]
      );
    } else {
      // Pairing key is saved — sync will work once the PC is reachable
      if (Platform.OS === 'android') ToastAndroid.show(`✅ Paired with ${pcName} (deferred)`, ToastAndroid.LONG);
      Alert.alert('Paired! 🔑',
        `Paired with ${pcName}.\n\nThe PC isn't reachable right now, but your pairing key is saved.\nClipboard sync will start automatically once FlyShelf is running.`,
        [{ text: 'OK' }]
      );
    }
  };

  const connectByCode = async (code: string) => {
    if (!code || code.trim().length !== 6) { Alert.alert('Invalid Code', 'Please enter a 6-character pairing code.'); return; }
    setIsPairing(true);
    if (Platform.OS === 'android') ToastAndroid.show('Looking up code...', ToastAndroid.SHORT);
    try {
      await ensureFirebaseAuth();
      const _authToken = await getFirebaseIdToken();
      const lookupUrl = `${firebaseDatabaseUrl}/pairing_codes/${code.toUpperCase().trim()}.json${_authToken ? `?auth=${_authToken}` : ''}`;
      console.log('[Pairing] Looking up code:', code.toUpperCase().trim(), 'token present:', !!_authToken);
      const res = await fetch(lookupUrl, { signal: createTimeoutSignal(10000) });
      console.log('[Pairing] Lookup response status:', res.status);
      const data = await res.json();
      console.log('[Pairing] Lookup data:', data ? JSON.stringify(data).substring(0, 200) : 'null');
      if (!data) { setIsPairing(false); Alert.alert('Code Not Found', 'No device found with this code.\nMake sure the code is correct and the other device is online.'); return; }

      // Check TTL (15 min) with absolute difference check to handle clock drift
      if (data.timestamp && Math.abs(NetworkClock.now() - data.timestamp) > 15 * 60 * 1000) {
        setIsPairing(false); Alert.alert('Code Expired', 'This code has expired. Generate a new one on the other device.'); return;
      }

      await executePairing({
        key: data.pairingKey, local: data.localUrl, global: data.globalUrl,
        pin: data.pin, name: data.deviceName, id: data.deviceId,
      });
      setIsConnectModalVisible(false);
      setPairingCodeInput('');
    } catch (err: any) {
      setIsPairing(false);
      const msg = err?.message || String(err);
      if (msg.includes('timeout') || msg.includes('AbortError')) {
        Alert.alert('Timeout', 'The request timed out. Make sure you have an active internet connection and try again.');
      } else if (msg.toLowerCase().includes('network') || msg.toLowerCase().includes('fetch')) {
        Alert.alert('Network Error', 'Could not reach the pairing server.\n\n• Check your internet connection\n• If on emulator, ensure network is enabled\n\nDetails: ' + msg);
      } else {
        Alert.alert('Error', 'Could not connect.\n\nDetails: ' + msg);
      }
    }
  };

  const generateMyPairingCode = async () => {
    const chars = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
    let code = '';
    const randomBytes = Crypto.getRandomBytes(6);
    for (let i = 0; i < 6; i++) code += chars[randomBytes[i] % chars.length];
    try {
      // Ensure Firebase auth is ready before writing
      await ensureFirebaseAuth();
      const myDeviceId = `Mobile_${(deviceName || 'Phone').replace(/[^a-zA-Z0-9_]/g, '_')}`;

      // Ensure we have a pairing key — generate one if this is a fresh install
      let currentKey = pairingKeyRef.current;
      if (!currentKey) {
        currentKey = await regeneratePairingKey();
        pairingKeyRef.current = currentKey;
      }

      const payload = {
        deviceId: myDeviceId,
        deviceName: deviceName || 'Phone',
        deviceType: 'Mobile',
        pairingKey: currentKey, // Use the ACTUAL pairing key, not deviceId
        localUrl: '',
        globalUrl: '',
        pin: '',
        uid: auth.currentUser?.uid || '', // Required by Firebase security rules
        timestamp: { '.sv': 'timestamp' }, // Write server-side timestamp to prevent client clock drift
      };
      const _pubToken = await getFirebaseIdToken();
      const writeUrl = `${firebaseDatabaseUrl}/pairing_codes/${code}.json${_pubToken ? `?auth=${_pubToken}` : ''}`;
      console.log('[Pairing] Writing code to Firebase:', code, 'token present:', !!_pubToken);
      const writeRes = await fetch(writeUrl, {
        method: 'PUT',
        signal: createTimeoutSignal(10000),
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });
      if (!writeRes.ok) {
        const errBody = await writeRes.text().catch(() => '');
        console.error('[Pairing] Firebase write failed:', writeRes.status, errBody);
        Alert.alert('Pairing Error', `Could not publish your code to the cloud (HTTP ${writeRes.status}).\n\nMake sure you have internet access.\n\nDetails: ${errBody}`);
        return;
      }
      // Verify the code was actually written by reading it back
      const verifyRes = await fetch(`${firebaseDatabaseUrl}/pairing_codes/${code}.json${_pubToken ? `?auth=${_pubToken}` : ''}`, { signal: createTimeoutSignal(8000) });
      const verifyData = await verifyRes.json();
      if (!verifyData || !verifyData.pairingKey) {
        console.error('[Pairing] Firebase write verification failed — code not found after write');
        Alert.alert('Pairing Error', 'Code was written but could not be verified. Please try again.');
        return;
      }
      console.log('[Pairing] Code verified in Firebase:', code, 'pairingKey:', verifyData.pairingKey?.substring(0, 8) + '...');
      setMyPairingCode(code);
      if (Platform.OS === 'android') ToastAndroid.show(`Code: ${code} (5 min) — Waiting for device...`, ToastAndroid.SHORT);

      if (connectionPollRef.current) clearInterval(connectionPollRef.current);
      if (connectionTimeoutRef.current) clearTimeout(connectionTimeoutRef.current);

      // ── Poll for incoming connections ──
      // When the PC enters our code, it writes its device info to pairing_codes/{code}/response.
      // We poll this node every 3s — no Firebase membership required since we own the code node.
      const pollForConnection = setInterval(async () => {
        try {
          const _pollToken = await getFirebaseIdToken();
          const codeRes = await fetch(
            `${firebaseDatabaseUrl}/pairing_codes/${code}.json${_pollToken ? `?auth=${_pollToken}` : ''}`,
            { signal: createTimeoutSignal(10000) }
          );
          const codeData = await codeRes.json();
          if (!codeData || !codeData.response) return;

          const resp = codeData.response;
          // Validate the response has the required fields
          if (!resp.deviceId || !resp.deviceName || !resp.pairingKey) return;

          console.log('[Pairing] PC response found in code node:', resp.deviceName, resp.deviceId);

          // Check not already paired
          const alreadyPaired = (await AsyncStorage.getItem('@pairedDevices') || '[]');
          const pairedList = JSON.parse(alreadyPaired);
          if (pairedList.some((d: any) => d.deviceId === resp.deviceId)) {
            // Already registered — just finish up
            clearInterval(pollForConnection);
            connectionPollRef.current = null;
            if (connectionTimeoutRef.current) { clearTimeout(connectionTimeoutRef.current); connectionTimeoutRef.current = null; }
            setMyPairingCode(null);
            return;
          }

          // Register the PC as a paired device
          await addPairedDevice({
            deviceId: resp.deviceId,
            deviceName: resp.deviceName,
            deviceType: resp.deviceType || 'PC',
            pairedAt: NetworkClock.now(),
            isPro: false,
            licenseKey: '',
          });

          // Save connection URLs for fast LAN sync
          if (resp.localUrl) await setSecureItem('pairedLocalUrl', resp.localUrl.startsWith('http') ? resp.localUrl : `http://${resp.localUrl}`);
          if (resp.globalUrl) await setSecureItem('pairedGlobalUrl', resp.globalUrl);

          // Adopt the shared pairing key so cloud clipboard sync works
          if (resp.pairingKey && resp.pairingKey !== pairingKeyRef.current) {
            pairingKeyRef.current = resp.pairingKey;
          }

          setPairedPcName(resp.deviceName);
          if (!isGlobalSyncEnabled) setGlobalSyncEnabled(true);
          if (Platform.OS === 'android') ToastAndroid.show(`✅ Paired with ${resp.deviceName}!`, ToastAndroid.LONG);

          clearInterval(pollForConnection);
          connectionPollRef.current = null;
          if (connectionTimeoutRef.current) {
            clearTimeout(connectionTimeoutRef.current);
            connectionTimeoutRef.current = null;
          }
          setMyPairingCode(null);
          // Clean up the pairing code from Firebase
          try { const _delToken = await getFirebaseIdToken(); await fetch(`${firebaseDatabaseUrl}/pairing_codes/${code}.json${_delToken ? `?auth=${_delToken}` : ''}`, { method: 'DELETE', signal: createTimeoutSignal(10000) }); } catch {}

        } catch (e) { syncLog('PAIR', `Connection poll error: ${(e as any)?.message || e}`); }
      }, 3000);
      connectionPollRef.current = pollForConnection;

      // Auto-expire after 5 min
      connectionTimeoutRef.current = setTimeout(async () => {
        clearInterval(pollForConnection);
        connectionPollRef.current = null;
        connectionTimeoutRef.current = null;
        try { const _expToken = await getFirebaseIdToken(); await fetch(`${firebaseDatabaseUrl}/pairing_codes/${code}.json${_expToken ? `?auth=${_expToken}` : ''}`, { method: 'DELETE', signal: createTimeoutSignal(10000) }); } catch {}
        setMyPairingCode(null);
      }, 5 * 60 * 1000);
    } catch (error: any) {
      const msg = error?.message || String(error);
      Alert.alert('Error', 'Could not generate code.\n\nDetails: ' + msg);
    }
  };

  const qrProcessingRef = useRef(false);
  const handleBarcodeScanned = async ({ data }: { data: string }) => {
    if (qrProcessingRef.current) return;
    qrProcessingRef.current = true;
    setIsQRScannerActive(false);

    // I-14 fix: try/finally guarantees the processing flag is always released.
    // Previously, if executePairing (or clipboard access) threw, the flag stayed
    // true forever and the QR scanner was permanently dead until app restart.
    try {
      // Try to parse as FlyShelf QR payload
      let qr: any = null;
      try { qr = JSON.parse(data); } catch {}

      if (qr && qr.app === 'FlyShelf') {
        // FlyShelf QR — do proper pairing
        await executePairing({ key: qr.key, local: qr.local, global: qr.global, pin: qr.pin, name: qr.name, id: qr.id });
        return;
      }

      // Not a FlyShelf QR — legacy behavior (copy text / open URL)
      await Clipboard.setStringAsync(data);
      if (Platform.OS === 'android') ToastAndroid.show('Copied QR content', ToastAndroid.SHORT);
      if (data.toLowerCase().startsWith('http://') || data.toLowerCase().startsWith('https://')) Linking.openURL(data).catch(() => {});
      setInputText(data);
    } catch (e: any) {
      syncLog('QR', `Scan handling failed: ${e?.message || e}`);
      Alert.alert('QR Error', e?.message || 'Failed to process QR code.');
    } finally {
      qrProcessingRef.current = false;
    }
  };



  // ─── Heavy Upload ───
  const CLOUD_CHUNK_SIZE = 5 * 1024 * 1024; // 5MB for Cloudflare
  // I-9 fix: 25MB LAN chunks were read as base64 (~33MB JS strings plus
  // copies) and risked OOM crashes on low-RAM devices. 8MB keeps the peak
  // around ~11MB while staying fast on LAN.
  const LAN_CHUNK_SIZE = 8 * 1024 * 1024; // 8MB for LAN
  const LAN_CHUNK_THRESHOLD = 50 * 1024 * 1024; // 50MB — files above this use chunked even on LAN

  const executeHeavyUpload = async (targetDeviceOrGlobal: any, payloadOverride?: any) => {
    try {
    const payload = payloadOverride || pendingUploadPayload;
    if (!payload) { syncLog('UPLOAD', 'No payload — skipping'); return; }
    setIsTargetModalVisible(false);
    setIsSending(true);
    const { uri: physicalPath, name, size, type } = payload;
    syncLog('UPLOAD', `Starting: ${name} (${type}) size=${size || '?'}`);
    // I-10 fix: track the ACTUAL temp path so the finally block can delete it.
    // The old cleanup rebuilt the name WITHOUT the timestamp prefix
    // (sync_${name} vs sync_${timestamp}_${name}), so temp copies under
    // SYNC_CACHE_BASE were never deleted - a disk leak on every upload.
    let hydratedPath = '';
    try {
      const safeName = `sync_${NetworkClock.now()}_` + name.replace(/[^a-zA-Z0-9.-]/g, '_');
      hydratedPath = `${SYNC_CACHE_BASE}${safeName}`;
      await FileSystem.copyAsync({ from: physicalPath, to: hydratedPath });

      if (targetDeviceOrGlobal === 'Global') {
        // Send to PC via LAN/Cloudflare (no Firebase Storage)
        const pc = activeDevices.find((d: any) => d.DeviceType === 'PC');
        if (!pc) { Alert.alert('No PC Found', 'No paired PC is online. Connect a PC first.'); setIsSending(false); setPendingUploadPayload(null); return; }
        let resolved = await resolveOptimalUrl(pc);
        if (!resolved) {
          if (lastWorkingPcUrlRef.current) {
            resolved = lastWorkingPcUrlRef.current;
          } else if (pcLocalIp?.trim()) {
            const rawParts = pcLocalIp.split(',').map(s => s.trim()).filter(Boolean);
            if (rawParts.length > 0) {
              const raw = rawParts[0];
              resolved = raw.startsWith('http') ? raw.replace(/\/$/, '') : `http://${raw.includes(':') ? raw : raw + ':8999'}`;
            }
          }
        }
        if (!resolved) { Alert.alert('PC Unreachable', 'Could not reach your PC. Make sure FlyShelf is running.'); setIsSending(false); setPendingUploadPayload(null); return; }
        // M-3: Retry for Global path single POST
        const uploadStartTime = performance.now();
        setUploadProgress({ name, progress: 0.1 });
        let uploadAttempt = 0;
        let uploadDone = false;
        while (uploadAttempt < 2 && !uploadDone) {
          uploadAttempt++;
          try {
            const uploadUrl = `${resolved}/api/sync_file?name=${encodeURIComponent(name)}&type=${encodeURIComponent(type)}&sourceDevice=${encodeURIComponent(deviceName || 'Mobile')}`;
            await FileSystem.uploadAsync(uploadUrl, hydratedPath, { httpMethod: 'POST', uploadType: 0 as any, headers: { 'X-Original-Date': NetworkClock.now().toString(), 'X-FlyShelf-Client': 'MobileCompanion', ...(pairingKeyRef.current ? { 'X-Pairing-Key': pairingKeyRef.current } : {}) } });
            uploadDone = true;
          } catch (retryErr) {
            if (uploadAttempt >= 2) throw retryErr;
            invalidatePcUrlCache();
            const freshUrl = await getCachedPcUrl();
            if (freshUrl) resolved = freshUrl;
            await new Promise(r => setTimeout(r, 1000));
          }
        }
        const uploadElapsedMs = performance.now() - uploadStartTime;
        const uploadSpeedMBps = (size || 0) > 0 && uploadElapsedMs > 0 ? ((size || 0) / (uploadElapsedMs / 1000) / (1024 * 1024)) : undefined;
        setUploadProgress({ name, progress: 1, speedMBps: uploadSpeedMBps });
      } else {
        // Direct device transfer (LAN or Cloudflare)
        let resolved = await resolveOptimalUrl(targetDeviceOrGlobal);
        if (!resolved) {
          if (lastWorkingPcUrlRef.current) {
            resolved = lastWorkingPcUrlRef.current;
          } else if (pcLocalIp?.trim()) {
            const rawParts = pcLocalIp.split(',').map(s => s.trim()).filter(Boolean);
            if (rawParts.length > 0) {
              const raw = rawParts[0];
              resolved = raw.startsWith('http') ? raw.replace(/\/$/, '') : `http://${raw.includes(':') ? raw : raw + ':8999'}`;
            }
          }
        }
        if (!resolved) { Alert.alert('Device Unreachable', 'Could not connect to this device. Make sure it is online.'); setIsSending(false); setPendingUploadPayload(null); return; }

        const isCloudflare = resolved.includes('trycloudflare.com');
        const CHUNK_SIZE = isCloudflare ? CLOUD_CHUNK_SIZE : LAN_CHUNK_SIZE;
        const fileSize = size || 0;
        // Issue #1: Use chunked upload for large files on LAN (>50MB) too, not just Cloudflare
        const useChunkedUpload = (isCloudflare && fileSize > CLOUD_CHUNK_SIZE) || (!isCloudflare && fileSize > LAN_CHUNK_THRESHOLD);

        if (useChunkedUpload) {
          // ── Chunked upload for large files over Cloudflare ──
          if (Platform.OS === 'android') ToastAndroid.show(`📦 Chunked upload: ${Math.ceil(fileSize / CHUNK_SIZE)} chunks`, ToastAndroid.SHORT);
          const sessionId = `${Date.now()}_${Math.random().toString(36).substring(2, 10)}`;
          const totalChunks = Math.ceil(fileSize / CHUNK_SIZE);
          const chunkedUploadStartTime = performance.now();
          setUploadProgress({ name, progress: 0 });

          for (let i = 0; i < totalChunks; i++) {
            const offset = i * CHUNK_SIZE;
            const length = Math.min(CHUNK_SIZE, fileSize - offset);

            // Read chunk as base64, write to temp file
            const chunkB64 = await FileSystem.readAsStringAsync(hydratedPath, {
              encoding: FileSystem.EncodingType.Base64,
              position: offset,
              length: length,
            });
            const chunkTempUri = `${FileSystem.cacheDirectory}chunk_${sessionId}_${i}`;
            await FileSystem.writeAsStringAsync(chunkTempUri, chunkB64, { encoding: FileSystem.EncodingType.Base64 });

            // Upload chunk with retries
            let attempt = 0;
            let done = false;
            while (attempt < 3 && !done) {
              attempt++;
              // Re-resolve URL on retry (tunnel URL may have changed)
              if (attempt > 1) {
                try {
                  // Re-resolve URL on retry (connection environment may have changed, e.g. left LAN)
                  const freshUrl = await getCachedPcUrl();
                  if (freshUrl && freshUrl !== resolved) {
                    syncLog('UPLOAD', `Switching to fresh URL: ${freshUrl}`);
                    resolved = freshUrl;
                  }
                } catch (e) { syncLog('UPLOAD', `URL re-resolution failed on retry: ${(e as any)?.message || e}`); }
              }
              try {
                const res = await FileSystem.uploadAsync(`${resolved}/api/upload_chunk`, chunkTempUri, {
                  httpMethod: 'POST',
                  uploadType: FileSystem.FileSystemUploadType.BINARY_CONTENT,
                  headers: {
                    'X-FlyShelf-Client': 'MobileCompanion',
                    'X-Upload-Session': sessionId,
                    'X-Chunk-Index': i.toString(),
                    ...(pairingKeyRef.current ? { 'X-Pairing-Key': pairingKeyRef.current } : {}),
                  }
                });
                if (res.status === 200) done = true;
                else throw new Error(`Chunk ${i + 1}/${totalChunks} failed: HTTP ${res.status}`);
              } catch (e) {
                if (attempt === 3) throw e;
                await new Promise(r => setTimeout(r, 1000));
              }
            }
            try { await FileSystem.deleteAsync(chunkTempUri, { idempotent: true }); } catch {}
            if (Platform.OS === 'android') ToastAndroid.show(`📤 Chunk ${i + 1}/${totalChunks} sent`, ToastAndroid.SHORT);
            const chunkElapsedMs = performance.now() - chunkedUploadStartTime;
            const bytesTransferred = Math.min((i + 1) * CHUNK_SIZE, fileSize);
            const chunkSpeedMBps = chunkElapsedMs > 0 ? (bytesTransferred / (chunkElapsedMs / 1000) / (1024 * 1024)) : undefined;
            setUploadProgress({ name, progress: (i + 1) / totalChunks, speedMBps: chunkSpeedMBps });
          }

          // Finalize — tell PC to merge all chunks (with retry — chunks are useless without this)
          let finalizeOk = false;
          for (let finAttempt = 0; finAttempt < 3 && !finalizeOk; finAttempt++) {
            try {
              const finRes = await fetchWithTimeout(`${resolved}/api/upload_finalize`, {
                method: 'POST',
                headers: {
                  'X-FlyShelf-Client': 'MobileCompanion',
                  'X-Upload-Session': sessionId,
                  'X-File-Name': encodeURIComponent(name),
                  'X-Original-Date': NetworkClock.now().toString(),
                  'X-Total-Chunks': totalChunks.toString(),
                  'X-Source-Device': encodeURIComponent(deviceName || 'Mobile'),
                  ...(pairingKeyRef.current ? { 'X-Pairing-Key': pairingKeyRef.current } : {}),
                }
              }, 15000);
              if (finRes.ok) { finalizeOk = true; }
              else if (finAttempt === 2) throw new Error(`Finalize failed after 3 attempts: ${finRes.status}`);
            } catch (finErr) {
              if (finAttempt === 2) throw finErr;
              await new Promise(r => setTimeout(r, 2000));
            }
          }
        } else {
          // ── Direct single POST (LAN or small Cloudflare files) ──
          // M-3: Add retry for single POST uploads
          const directUploadStartTime = performance.now();
          setUploadProgress({ name, progress: 0.1 });
          let uploadAttempt = 0;
          let uploadDone = false;
          while (uploadAttempt < 2 && !uploadDone) {
            uploadAttempt++;
            try {
              const uploadUrl = `${resolved}/api/sync_file?name=${encodeURIComponent(name)}&type=${encodeURIComponent(type)}&sourceDevice=${encodeURIComponent(deviceName || 'Mobile')}`;
              await FileSystem.uploadAsync(uploadUrl, hydratedPath, { httpMethod: 'POST', uploadType: 0 as any, headers: { 'X-Original-Date': NetworkClock.now().toString(), 'X-FlyShelf-Client': 'MobileCompanion', ...(pairingKeyRef.current ? { 'X-Pairing-Key': pairingKeyRef.current } : {}) } });
              uploadDone = true;
            } catch (retryErr) {
              if (uploadAttempt >= 2) throw retryErr;
              invalidatePcUrlCache();
              const freshUrl = await getCachedPcUrl();
              if (freshUrl) resolved = freshUrl;
              await new Promise(r => setTimeout(r, 1000));
            }
          }
          const directElapsedMs = performance.now() - directUploadStartTime;
          const directSpeedMBps = (size || 0) > 0 && directElapsedMs > 0 ? ((size || 0) / (directElapsedMs / 1000) / (1024 * 1024)) : undefined;
          setUploadProgress({ name, progress: 1, speedMBps: directSpeedMBps });
        }
      }
      if (Platform.OS === 'android') ToastAndroid.show(`✅ ${name} sent!`, ToastAndroid.SHORT);
    } catch (err: any) { syncLog('UPLOAD', `FAILED: ${err?.message}`); Alert.alert('Upload Failed', err?.message || 'Unknown error');
    } finally { if (hydratedPath) { try { await FileSystem.deleteAsync(hydratedPath, { idempotent: true }); } catch {} } }
    } catch (outerErr: any) { syncLog('UPLOAD', `CRASH: ${outerErr?.message}`); Alert.alert('Error', outerErr?.message || 'Unexpected error'); }
    setIsSending(false);
    setPendingUploadPayload(null);
    setUploadProgress(null);
  };

  // ─── Clip Visibility Filter (with category + search) ───
  const clipFilter = (c: ClipItem) => {
    // File types (PDF, Document, etc.): always show if they have a Title — they are continuously re-synced from PC
    if (['Pdf', 'Document', 'File', 'Video', 'Audio', 'Archive', 'Presentation'].includes(c.Type) && c.Title) {
      // Still respect wipe timestamp
      if (!((c.Timestamp || 0) >= localWipeTimestamp || c.IsPinned)) return false;
    } else {
      const isVisible = (c.IsPinned || (c.Timestamp || 0) >= localWipeTimestamp) && (!c.id || !localDeletedIds.has(c.id)) && (c.Raw || c.Title);
      if (!isVisible) return false;
    }
    // Filter out Windows file paths (useless on Android) — allow Android file:// URIs
    const rawStr = c.Raw || '';
    if (/^[A-Z]:\\/.test(rawStr)) return false;
    if (rawStr.startsWith('file:///') && /^file:\/\/\/[A-Z]:/.test(rawStr)) return false;
    // Filter out stale download progress cards
    if (rawStr.startsWith('Downloading from ') && rawStr.endsWith('...')) return false;
    // Filter out stale image clips with expired PC server URLs (no local cache)
    if ((c.Type === 'Image' || c.Type === 'ImageLink' || c.Type === 'QRCode') && !c.CachedUri) {
      if (rawStr.startsWith('file:///') && !(/^file:\/\/\/[A-Z]:/.test(rawStr))) { /* keep */ }
      else if (rawStr.startsWith('https://')) { /* keep */ }
      else if (rawStr.startsWith('http://') && /^http:\/\/\d+\.\d+\.\d+\.\d+:\d+\//.test(rawStr)) return false;
      else if (!rawStr && !c.PreviewUrl && !c.DownloadUrl) return false;
    }
    if (!c.Raw && !c.Title) return false;

    // ── Category filter ──
    if (feedCategory !== 'All') {
      const lowerTitle = (c.Title || c.Raw || '').toLowerCase();
      if (feedCategory === 'Text') {
        if (!['Text', 'Url', 'Code'].includes(c.Type)) return false;
      } else if (feedCategory === 'Images') {
        if (!['Image', 'ImageLink', 'QRCode'].includes(c.Type)) return false;
      } else if (feedCategory === 'Docs') {
        // PDF, Word, PPT, Excel, Archive, APK, etc.
        const isDocType = ['Pdf', 'Document', 'File', 'Archive', 'Presentation', 'Video', 'Audio'].includes(c.Type);
        const isDocExt = lowerTitle.match(/\.(pdf|doc|docx|ppt|pptx|xls|xlsx|txt|csv|zip|rar|7z|apk|tar|gz|mp4|mp3|mkv)$/);
        if (!isDocType && !isDocExt) return false;
      }
    }

    // ── Search filter ──
    if (feedSearch.trim()) {
      const q = feedSearch.trim().toLowerCase();
      const searchTarget = `${c.Title || ''} ${c.Raw || ''} ${c.Type || ''}`.toLowerCase();
      if (!searchTarget.includes(q)) return false;
    }

    return true;
  };

  // ─── Memoized filtered clips: avoid re-filtering on every render ───
  const filteredClips = useMemo(() => clips.filter(clipFilter), [clips, feedCategory, feedSearch, localWipeTimestamp, localDeletedIds]);

  // ─── Memoized renderItem for FlashList ───
  const renderClipItem = useCallback(({ item, index: itemIndex }: any) => {
    let iconName: any = 'document-text-outline', iconColor: string = colors.text.tertiary;
    const lowerTit = (item.Title || item.Raw || '').toLowerCase();
    const isApk = lowerTit.endsWith('.apk');
    const isPdf = item.Type === 'Pdf' || lowerTit.endsWith('.pdf');
    const isDoc = lowerTit.endsWith('.doc') || lowerTit.endsWith('.docx') || item.Type === 'Document';
    if (item.Type === 'ImageLink' || item.Type === 'Image') { iconName = 'image'; iconColor = '#ec4899'; }
    else if (item.Type === 'Url') { iconName = 'globe-outline'; iconColor = '#0EA5E9'; }
    else if (isPdf) { iconName = 'document-text'; iconColor = colors.accent.error; }
    else if (isApk) { iconName = 'cube-outline'; iconColor = colors.accent.success; }
    else if (isDoc) { iconName = 'document'; iconColor = colors.accent.info; }
    else if (['File', 'Archive', 'Video', 'Presentation'].includes(item.Type)) { iconName = 'folder'; iconColor = colors.accent.warning; }
    else if (item.Type === 'Code') { iconName = 'code-slash-outline'; iconColor = colors.accent.success; }
    else if (item.Type === 'QRCode') { iconName = 'qr-code-outline'; iconColor = colors.type.image; }

    const mediaUrl = getMediaUrlForItem(item);
    const transferId = item.id || (item.Title || '').replace(/[^a-zA-Z0-9.-]/g, '_');
    const incomingProgress = incomingTransferProgress[transferId];
    const isIncomingTransfer = incomingProgress !== undefined && incomingProgress < 1;
    const heavyFileTypes = ['Pdf', 'Document', 'Archive', 'Video', 'Audio', 'File', 'Presentation'];
    const isHeavyFile = heavyFileTypes.includes(item.Type) || (item.Title || '').toLowerCase().endsWith('.apk');

    return (
      <View style={{ position: 'relative' }}>
      <AnimatedCard
        index={itemIndex}
        style={[styles.clipCard, { flexDirection: 'column', alignItems: 'stretch' }, isMultiSelectMode && selectedItemIds.has(item.id || '') && { borderColor: colors.accent.primary, borderWidth: 1.5 }]}
        onPress={() => { const itemKey = item.id || `idx_${itemIndex}`; if (isMultiSelectMode) toggleSelectItem(item.id || ''); else if (activeOptionsId === itemKey) setActiveOptionsId(null); else setActiveOptionsId(itemKey); }}
        onLongPress={() => { if (!isMultiSelectMode) { enterMultiSelect(item.id || ''); setActiveOptionsId(null); } }}
        skipEntrance={itemIndex > 12}
      >
        <View style={{ flexDirection: 'row', alignItems: 'center', width: '100%', marginBottom: (item.Type === 'Image' || item.Type === 'ImageLink') ? space.sm : 0 }}>
          {isMultiSelectMode && (
            <View style={{ marginRight: 8, width: 22, height: 22, borderRadius: 11, backgroundColor: selectedItemIds.has(item.id || '') ? '#4A62EB' : 'rgba(255,255,255,0.1)', borderWidth: 2, borderColor: selectedItemIds.has(item.id || '') ? '#4A62EB' : '#4C5361', alignItems: 'center', justifyContent: 'center' }}>
              {selectedItemIds.has(item.id || '') && <Ionicons name="checkmark" size={10} color="#FFF" />}
            </View>
          )}
          
          {/* Left-aligned type icon */}
          <View style={[styles.clipIconContainer, { backgroundColor: iconColor + '15', borderColor: iconColor + '30', marginRight: space.md }]}>
            <Ionicons name={iconName} size={20} color={iconColor} />
          </View>
 
          {/* Header Info */}
          <View style={styles.clipContentContainer}>
            <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center' }}>
              <Text style={styles.clipType}>{item.Type}</Text>
              <Text style={styles.clipTime}>{item.Time || ''}</Text>
            </View>
            
            {/* Device / Transfer Source indicator */}
            <Text style={{ color: colors.text.tertiary, fontSize: 10, fontFamily: font.semibold, marginTop: 2 }}>
              {item.SourceDeviceName ? `From ${item.SourceDeviceName}` : 'Local Companion'}
            </Text>
          </View>
          
          {/* Transfer method badge (LAN/Cloud) */}
          {(() => {
            const via = item._receivedVia || '';
            let viaIcon: any = 'send-outline';
            let label = '';
            let badgeColor: string = colors.text.tertiary;
            let badgeBg = 'rgba(15,17,21,0.85)';
            if (via === 'LAN') {
              viaIcon = 'wifi'; label = 'LAN'; badgeColor = colors.accent.success; badgeBg = 'rgba(16,185,129,0.15)';
            } else if (via === 'Cloud') {
              viaIcon = 'cloud'; label = 'Cloud'; badgeColor = colors.accent.info; badgeBg = 'rgba(59,130,246,0.15)';
            } else if (via === 'Local') {
              viaIcon = 'clipboard-outline'; label = 'Local'; badgeColor = colors.type.image; badgeBg = 'rgba(139,92,246,0.15)';
            } else {
              // Legacy items without _receivedVia — infer from content
              const rawUrl = item.Raw || '';
              if (rawUrl.includes('trycloudflare.com') || rawUrl.includes('firebase')) {
                viaIcon = 'cloud'; label = 'Cloud'; badgeColor = colors.accent.info; badgeBg = 'rgba(59,130,246,0.15)';
              } else if (rawUrl.startsWith('http://192.') || rawUrl.startsWith('http://10.') || rawUrl.startsWith('http://172.')) {
                viaIcon = 'wifi'; label = 'LAN'; badgeColor = colors.accent.success; badgeBg = 'rgba(16,185,129,0.15)';
              }
            }
            if (!label) return null;
            return (
              <View style={{ flexDirection: 'row', alignItems: 'center', backgroundColor: badgeBg, borderRadius: 8, paddingHorizontal: 7, paddingVertical: 3, gap: 4, marginLeft: 8 }}>
                <Ionicons name={viaIcon} size={11} color={badgeColor} />
                <Text style={{color: badgeColor, fontSize: 9, fontWeight: '800', letterSpacing: 0.5}}>{label}</Text>
              </View>
            );
          })()}
        </View>

        {/* Card Content body */}
        <View style={{ flex: 1, paddingLeft: isMultiSelectMode ? 30 : 0 }}>
          {(item.Type === 'Image' || item.Type === 'ImageLink') ? (() => {
            const imgUri = mediaUrl || item.CachedUri || item.Raw || '';
            if (!imgUri) return <View style={{ marginBottom: 8, height: 100, borderRadius: 12, backgroundColor: '#1C202B', justifyContent: 'center', alignItems: 'center' }}><Ionicons name="image-outline" size={32} color={colors.text.tertiary} /><Text style={{color: '#8A8F98', fontSize: 12, marginTop: 8}}>No image URL</Text></View>;
            return <CachedImage imgUri={imgUri} onPress={() => setExpandedImage(imgUri)} />;
          })() : null}
          
          {(item.Type !== 'Image' && item.Type !== 'ImageLink') && (
            <Text style={[styles.clipTitle, { marginTop: space.sm }]} numberOfLines={6}>{item.Raw || item.Title || `${item.Type || 'Clip'} from ${item.SourceDeviceName || 'Unknown'}`}</Text>
          )}
          
          {isIncomingTransfer && isHeavyFile && (
            <View style={{ marginTop: space.sm, borderRadius: 12, overflow: 'hidden', zIndex: 20 }}>
              <View style={{height: 28, backgroundColor: 'rgba(15,17,21,0.92)', flexDirection: 'row', alignItems: 'center', paddingHorizontal: 12}}>
                <ActivityIndicator size="small" color="#4A62EB" style={{marginRight: 8}} /><Text style={{color: '#8A8F98', fontSize: 11, fontWeight: '600', flex: 1}}>Receiving file...</Text>
                <Text style={{color: '#4A62EB', fontSize: 12, fontWeight: '800'}}>{Math.round((incomingProgress || 0) * 100)}%</Text>
              </View>
              <View style={{height: 3, backgroundColor: 'rgba(74,98,235,0.15)'}}><View style={{height: 3, backgroundColor: '#4A62EB', width: `${Math.round((incomingProgress || 0) * 100)}%`, borderRadius: 2}} /></View>
            </View>
          )}
        </View>
      </AnimatedCard>
        {activeOptionsId === (item.id || `idx_${itemIndex}`) && !(isIncomingTransfer && isHeavyFile) && (
          <View style={{ position: 'absolute', right: 10, top: 10, flexDirection: 'row', backgroundColor: 'rgba(20,24,36,0.95)', borderRadius: 12, padding: 8, gap: 8, zIndex: 50 }}>
            {['Image', 'ImageLink'].includes(item.Type) && (
              <TouchableOpacity onPress={() => { handleConvertImageToPdf(item); setActiveOptionsId(null); }} style={[styles.actionBtnIcon, {backgroundColor: '#EF444433'}]}>
                <Ionicons name="document-outline" size={18} color={colors.accent.error} />
              </TouchableOpacity>
            )}
            {['Text', 'Code', 'Url'].includes(item.Type) && (
              <TouchableOpacity onPress={() => {
                const query = item.Raw || item.Title || '';
                if (query) {
                  Linking.openURL(`https://www.google.com/search?q=${encodeURIComponent(query)}`).catch(() => {});
                }
                setActiveOptionsId(null);
              }} style={[styles.actionBtnIcon, {backgroundColor: '#10B98133'}]}>
                <Ionicons name="search" size={18} color={colors.accent.success} />
              </TouchableOpacity>
            )}
            <TouchableOpacity onPress={async () => {
              try {
                if (!item.id) { if (Platform.OS === 'android') ToastAndroid.show("Pinning is restricted to Cloud Hub payloads.", ToastAndroid.SHORT); return; }
                await update(ref(database, `${clipboardPath()}/${item.id}`), { IsPinned: !item.IsPinned });
                setClips(prev => prev.map(c => c.id === item.id ? {...c, IsPinned: !c.IsPinned} : c));
                if (Platform.OS === 'android') ToastAndroid.show(item.IsPinned ? "Unpinned" : "Pinned!", ToastAndroid.SHORT);
              } catch(e: any) { syncLog('PIN', `Pin/unpin failed: ${e?.message || e}`); }
              setActiveOptionsId(null);
            }} style={[styles.actionBtnIcon, {backgroundColor: item.IsPinned ? '#F59E0B33' : '#2A2F3A'}]}>
              <Ionicons name={item.IsPinned ? "pin" : "pin-outline"} size={18} color={item.IsPinned ? colors.accent.warning : colors.text.tertiary} />
            </TouchableOpacity>
            <TouchableOpacity onPress={async () => {
              const contentStr = item.Raw || item.Title || '';
              if (item.Type === 'Image' || item.Type === 'ImageLink') {
                try { const src = item.CachedUri || mediaUrl || item.Raw; if (src) { if (src.startsWith('file://') || src.startsWith('/')) { const b64 = await FileSystem.readAsStringAsync(src.startsWith('file://') ? src : `file://${src}`, { encoding: FileSystem.EncodingType.Base64 }); await Clipboard.setImageAsync(b64); } else { const localUri = `${SYNC_CACHE_BASE}copy_${Date.now()}.png`; const dl = await FileSystem.downloadAsync(src, localUri, { headers: { 'X-FlyShelf-Client': 'MobileCompanion', 'X-Pairing-Key': pairingKeyRef.current || '' } }); const b64 = await FileSystem.readAsStringAsync(dl.uri, { encoding: FileSystem.EncodingType.Base64 }); await Clipboard.setImageAsync(b64); } if (Platform.OS === 'android') ToastAndroid.show("Image Copied", ToastAndroid.SHORT); } } catch(e) { await Clipboard.setStringAsync(contentStr); if (Platform.OS === 'android') ToastAndroid.show("URL Copied", ToastAndroid.SHORT); }
              } else { await Clipboard.setStringAsync(contentStr); if (Platform.OS === 'android') ToastAndroid.show("Copied!", ToastAndroid.SHORT); }
              setActiveOptionsId(null);
            }} style={[styles.actionBtnIcon, {backgroundColor: '#4A62EB33'}]}>
              <Ionicons name="copy-outline" size={18} color={colors.accent.primary} />
            </TouchableOpacity>
            {(item.Type === 'Url' || (item.Raw && item.Raw.startsWith('http'))) && (
              <TouchableOpacity onPress={() => { Linking.openURL(item.Raw).catch(() => {}); setActiveOptionsId(null); }} style={[styles.actionBtnIcon, {backgroundColor: '#0EA5E933'}]}>
                <Ionicons name="open-outline" size={18} color={colors.accent.info} />
              </TouchableOpacity>
            )}
            {['Pdf', 'Document', 'File', 'Video', 'Audio', 'Archive', 'Presentation'].includes(item.Type) && (
              <TouchableOpacity onPress={async () => {
                try {
                  const safeName = (item.Title || '').replace(/[^a-zA-Z0-9._-]/g, '_');
                  const subfolder = item.Type === 'Pdf' ? 'PDFs' : item.Type === 'Video' ? 'Videos' : item.Type === 'Audio' ? 'Audio' : 'Documents';
                  const filePath = item.CachedUri || `${DOWNLOAD_BASE}${subfolder}/${safeName}`;
                  const info = await FileSystem.getInfoAsync(filePath);
                  if (info.exists) {
                    const ext = (item.Title || '').split('.').pop()?.toLowerCase() || '';
                    const mimeMap: Record<string, string> = {
                      'pdf': 'application/pdf', 'doc': 'application/msword',
                      'docx': 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
                      'xls': 'application/vnd.ms-excel', 'xlsx': 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
                      'ppt': 'application/vnd.ms-powerpoint', 'pptx': 'application/vnd.openxmlformats-officedocument.presentationml.presentation',
                      'mp4': 'video/mp4', 'mkv': 'video/x-matroska', 'avi': 'video/x-msvideo',
                      'mp3': 'audio/mpeg', 'wav': 'audio/wav', 'zip': 'application/zip',
                      'rar': 'application/x-rar-compressed', '7z': 'application/x-7z-compressed',
                      'txt': 'text/plain', 'csv': 'text/csv', 'json': 'application/json',
                      'jpg': 'image/jpeg', 'jpeg': 'image/jpeg', 'png': 'image/png',
                    };
                    const mimeType = mimeMap[ext] || 'application/*';
                    const fileUri = filePath.startsWith('file://') ? filePath : `file://${filePath}`;
                    const contentUri = await FileSystem.getContentUriAsync(fileUri);
                    await IntentLauncher.startActivityAsync('android.intent.action.VIEW', {
                      data: contentUri,
                      flags: 1,
                      type: mimeType,
                    });
                  } else {
                    if (Platform.OS === 'android') ToastAndroid.show('File not yet downloaded — syncing...', ToastAndroid.SHORT);
                  }
                } catch (e: any) {
                  try {
                    const safeName = (item.Title || '').replace(/[^a-zA-Z0-9._-]/g, '_');
                    const subfolder = item.Type === 'Pdf' ? 'PDFs' : item.Type === 'Video' ? 'Videos' : item.Type === 'Audio' ? 'Audio' : 'Documents';
                    const filePath = item.CachedUri || `${DOWNLOAD_BASE}${subfolder}/${safeName}`;
                    if (await Sharing.isAvailableAsync()) {
                      await Sharing.shareAsync(filePath, { dialogTitle: `Open ${item.Title}` });
                    } else {
                      if (Platform.OS === 'android') ToastAndroid.show(`No app found to open this file`, ToastAndroid.SHORT);
                    }
                  } catch (shareErr: any) {
                    if (Platform.OS === 'android') ToastAndroid.show(`Cannot open: ${shareErr?.message || 'unknown error'}`, ToastAndroid.SHORT);
                  }
                }
                setActiveOptionsId(null);
              }} style={[styles.actionBtnIcon, {backgroundColor: '#6366F133'}]}>
                <Ionicons name="scan-outline" size={18} color={colors.accent.primary} />
              </TouchableOpacity>
            )}
            <TouchableOpacity onPress={async () => {
              if (item.id) {
                setLocalDeletedIds(prev => { const n = new Set(prev); n.add(item.id!); AsyncStorage.setItem('localDeletedIds', JSON.stringify([...n])).catch(() => {}); return n; });
                if (isGlobalSyncEnabled && pairingKeyRef.current) { try { await remove(ref(database, `${clipboardPath()}/${item.id}`)); } catch(e) {} }
              } else {
                setClips(prev => prev.filter(c => !(c.Title === item.Title && c.Raw === item.Raw && c.Timestamp === item.Timestamp)));
              }
              setActiveOptionsId(null);
              if (Platform.OS === 'android') ToastAndroid.show("Deleted", ToastAndroid.SHORT);
            }} style={[styles.actionBtnIcon, {backgroundColor: '#EF444433'}]}>
              <Ionicons name="trash-outline" size={18} color={colors.accent.error} />
            </TouchableOpacity>
          </View>
        )}
      </View>
    );
  }, [activeOptionsId, isMultiSelectMode, selectedItemIds, incomingTransferProgress, isGlobalSyncEnabled, getMediaUrlForItem, setExpandedImage, handleConvertImageToPdf]);

  // ════════════════════════════════════════════════════════
  const scrollY = useSharedValue(0);
  const scrollHandler = useAnimatedScrollHandler({ onScroll: (e) => { scrollY.value = e.contentOffset.y; } });

  // RENDER
  // ════════════════════════════════════════════════════════
  return (
    <LinearGradient colors={[colors.bg.base, colors.bg.baseEnd]} style={{ flex: 1 }}>
    <View style={[styles.container, { backgroundColor: 'transparent' }]}>
      {/* Device Name Setup Modal */}
      <Modal visible={!deviceName} animationType="fade" transparent={true}>
        <View style={styles.modalOverlay}><View style={styles.modalContent}>
          <Text style={styles.modalTitle}>Name this Device</Text>
          <Text style={styles.modalSubtitle}>Identify this device in the FlyShelf network.</Text>
          <TextInput style={styles.modalInput} value={setupName} onChangeText={setSetupName} placeholder="e.g. Galaxy S23" placeholderTextColor="#4C5361" autoFocus accessibilityLabel="Device name" accessibilityRole="text" />
          <TouchableOpacity style={styles.modalButton} onPress={() => { if(setupName.trim()) setDeviceName(setupName.trim()); }} accessibilityLabel="Get started" accessibilityRole="button"><Text style={styles.modalButtonText}>Get Started</Text></TouchableOpacity>
        </View></View>
      </Modal>

      {/* Target Device Selection Modal */}
      <Modal visible={isTargetModalVisible} animationType="slide" transparent={true}>
        <View style={styles.modalOverlay}><View style={styles.modalContent}>
          <Text style={styles.modalTitle}>Select Target Node</Text>
          <Text style={styles.modalSubtitle}>Where do you want to transfer this payload?</Text>
          <TouchableOpacity style={styles.targetOption} onPress={() => executeHeavyUpload('Global')} accessibilityLabel="Send to all devices via cloud" accessibilityRole="button">
            <Ionicons name="cloud" size={24} color={colors.accent.primary} />
            <View style={{marginLeft: 12}}><Text style={{color: '#FFF', fontSize: 16, fontWeight: '600'}}>Cloud Hub</Text><Text style={{color: '#8A8F98', fontSize: 12}}>10MB Limit. Shared across your ecosystem.</Text></View>
          </TouchableOpacity>
          <Text style={{color: '#8A8F98', fontSize: 12, marginTop: 16, marginBottom: 8, fontWeight: '700', textTransform: 'uppercase'}}>Active Proxy Endpoints</Text>
          {activeDevices.map((device, i) => {
            const connType = getConnectionType(device, pcLocalIp);
            return (
              <TouchableOpacity key={i} style={styles.targetOption} onPress={() => executeHeavyUpload(device)}>
                <Ionicons name={device.DeviceType === 'PC' ? 'laptop-outline' : 'phone-portrait-outline'} size={24} color={connectionColors[connType]} />
                <View style={{marginLeft: 12, flex: 1}}>
                  <Text style={{color: '#FFF', fontSize: 16, fontWeight: '600'}}>{String(device.DeviceName || device.deviceName)}</Text>
                  <View style={{flexDirection: 'row', alignItems: 'center', gap: 6, marginTop: 2}}>
                    <View style={{backgroundColor: connectionColors[connType] + '22', borderRadius: 4, paddingHorizontal: 6, paddingVertical: 1}}><Text style={{color: connectionColors[connType], fontSize: 10, fontWeight: '700'}}>{connType}</Text></View>
                    <Text style={{color: '#8A8F98', fontSize: 12}}>{connType === 'LAN' ? 'Same network · Direct transfer' : 'Remote · Via tunnel'}</Text>
                  </View>
                </View>
              </TouchableOpacity>
            );
          })}
          <TouchableOpacity style={[styles.modalButton, {backgroundColor: '#2A2F3A', marginTop: 10}]} onPress={() => { setIsTargetModalVisible(false); setPendingUploadPayload(null); }} accessibilityLabel="Cancel" accessibilityRole="button"><Text style={styles.modalButtonText}>Cancel</Text></TouchableOpacity>
        </View></View>
      </Modal>

      {/* Camera Options Modal */}
      <Modal visible={isCameraOptionsVisible} animationType="slide" transparent={true}>
        <View style={styles.modalOverlay}><View style={styles.modalContent}>
          <Text style={styles.modalTitle}>Capture Mode</Text>
          <Text style={styles.modalSubtitle}>Take a photo to transfer or scan a data code.</Text>
          <TouchableOpacity style={styles.targetOption} onPress={launchDirectCamera} accessibilityLabel="Take photo" accessibilityRole="button">
            <Ionicons name="camera" size={24} color={colors.accent.warning} />
            <View style={{marginLeft: 12}}><Text style={{color: '#FFF', fontSize: 16, fontWeight: '600'}}>Take Photo</Text><Text style={{color: '#8A8F98', fontSize: 12}}>Instantly transfer a camera image.</Text></View>
          </TouchableOpacity>
          <TouchableOpacity style={styles.targetOption} onPress={launchQRScanner} accessibilityLabel="Scan QR code" accessibilityRole="button">
            <Ionicons name="qr-code-outline" size={24} color={colors.type.image} />
            <View style={{marginLeft: 12}}><Text style={{color: '#FFF', fontSize: 16, fontWeight: '600'}}>Scan QR Code</Text><Text style={{color: '#8A8F98', fontSize: 12}}>Pair with PC or extract data.</Text></View>
          </TouchableOpacity>
          <TouchableOpacity style={[styles.modalButton, {backgroundColor: '#2A2F3A', marginTop: 10}]} onPress={() => setIsCameraOptionsVisible(false)} accessibilityLabel="Cancel" accessibilityRole="button"><Text style={styles.modalButtonText}>Cancel</Text></TouchableOpacity>
        </View></View>
      </Modal>

      {/* Connect Device Modal */}
      <Modal visible={isConnectModalVisible} animationType="slide" transparent={true}>
        <View style={styles.modalOverlay}><View style={styles.modalContent}>
          <Text style={styles.modalTitle}>Connect Device</Text>
          <Text style={styles.modalSubtitle}>Pair once — stays connected forever</Text>

          {/* Option 1: Scan QR */}
          <TouchableOpacity style={styles.targetOption} onPress={launchQRScanner}>
            <Ionicons name="qr-code-outline" size={24} color={colors.type.image} />
            <View style={{marginLeft: 12}}>
              <Text style={{color: '#FFF', fontSize: 16, fontWeight: '600'}}>Scan QR Code</Text>
              <Text style={{color: '#8A8F98', fontSize: 12}}>Point camera at QR on your PC</Text>
            </View>
          </TouchableOpacity>

          {/* Option 2: Enter Code */}
          <View style={{marginTop: 16}}>
            <Text style={{color: '#8A8F98', fontSize: 12, fontWeight: '700', textTransform: 'uppercase', marginBottom: 8}}>Or Enter Code</Text>
            <View style={{flexDirection: 'row', gap: 10}}>
              <TextInput
                style={{flex: 1, backgroundColor: '#0F1115', color: '#FFF', fontSize: 22, fontWeight: '800',
                  borderRadius: 12, paddingHorizontal: 16, paddingVertical: 12, borderWidth: 1,
                  borderColor: '#2A2F3A', textAlign: 'center', letterSpacing: 6}}
                value={pairingCodeInput}
                onChangeText={setPairingCodeInput}
                placeholder="A7K9M2"
                placeholderTextColor="#4C5361"
                maxLength={6}
                autoCapitalize="characters"
              />
              <TouchableOpacity
                style={{backgroundColor: isPairing ? '#4C5361' : '#4A62EB', borderRadius: 12, paddingHorizontal: 20, justifyContent: 'center'}}
                onPress={() => { if (pairingCodeInput.length === 6) connectByCode(pairingCodeInput); }}
                disabled={isPairing}
              >
                {isPairing ? <ActivityIndicator size="small" color="#FFF" /> : <Text style={{color: '#FFF', fontWeight: '700', fontSize: 14}}>Connect</Text>}
              </TouchableOpacity>
            </View>
          </View>

          {/* This phone's code */}
          <View style={{marginTop: 20, padding: 14, backgroundColor: '#0F1115', borderRadius: 12, borderWidth: 1, borderColor: '#10B98133'}}>
            <Text style={{color: '#10B981', fontSize: 12, fontWeight: '700', marginBottom: 6}}>Your Phone's Code</Text>
            {myPairingCode ? (
              <Text style={{color: '#FFF', fontSize: 28, fontWeight: '900', letterSpacing: 8, textAlign: 'center'}}>{myPairingCode}</Text>
            ) : (
              <TouchableOpacity
                style={{backgroundColor: '#10B98122', borderRadius: 10, paddingVertical: 10, alignItems: 'center'}}
                onPress={generateMyPairingCode}
              >
                <Text style={{color: '#10B981', fontWeight: '700', fontSize: 14}}>🔑 Generate Code</Text>
              </TouchableOpacity>
            )}
            <Text style={{color: '#8A8F98', fontSize: 11, marginTop: 6, textAlign: 'center'}}>Enter this code on your PC to connect</Text>
          </View>

          {/* Connected status */}
          {pairedPcName && (
            <View style={{marginTop: 12, padding: 10, backgroundColor: '#10B98111', borderRadius: 10, flexDirection: 'row', alignItems: 'center', gap: 8}}>
              <View style={{width: 8, height: 8, borderRadius: 4, backgroundColor: '#10B981'}} />
              <Text style={{color: '#10B981', fontSize: 13, fontWeight: '600'}}>Paired with {pairedPcName}{isPairedPcPro ? ' (Pro)' : ' (Free)'}</Text>
            </View>
          )}

          <TouchableOpacity style={[styles.modalButton, {backgroundColor: '#2A2F3A', marginTop: 16}]}
            onPress={() => { setIsConnectModalVisible(false); setPairingCodeInput(''); }}>
            <Text style={styles.modalButtonText}>Close</Text>
          </TouchableOpacity>
        </View></View>
      </Modal>

      {/* QR Scanner */}
      {isQRScannerActive && (
        <Modal visible={isQRScannerActive} animationType="fade" transparent={false}>
          <View style={{flex: 1, backgroundColor: '#000'}}>
            <CameraView style={{flex: 1}} facing="back" barcodeScannerSettings={{ barcodeTypes: ["qr"] }} onBarcodeScanned={handleBarcodeScanned} />
            <TouchableOpacity style={{position: 'absolute', bottom: 50, alignSelf: 'center', backgroundColor: '#EF4444', padding: 15, borderRadius: 30}} onPress={() => { qrProcessingRef.current = false; setIsQRScannerActive(false); }} accessibilityLabel="Close QR scanner" accessibilityRole="button">
              <Text style={{color: '#fff', fontWeight: 'bold', fontSize: 16}}>Cancel Scan</Text>
            </TouchableOpacity>
          </View>
        </Modal>
      )}

      <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : undefined} style={{flex: 1}}>
        {/* Header */}
        <ScreenHeader
          title="FlyShelf"
          subtitle={pairingKeyRef.current ? (pairedPcName ? `Connected to ${pairedPcName}${isPairedPcPro ? ' (Pro)' : ' (Free)'}${connectionInfo ? ` ${connectionInfo.type === 'LAN' ? '🟢' : '🟡'} ${connectionInfo.type === 'LAN' ? `LAN${(() => { try { const m = connectionInfo.url.match(/:\/\/([^:/]+)/); return m ? ' ' + m[1] : ''; } catch { return ''; } })()} • ` : 'Cloud • '}${connectionInfo.latencyMs}ms` : ''}` : 'Cloud Active') : '⚠ Not Paired'}
          scrollY={scrollY}
          statusBadge={
            pairedDevices.length > 0 ? (
              <View style={{flexDirection: 'row', alignItems: 'center', marginTop: 3, gap: 6}}>
                {(() => {
                  const onlinePcs = pairedDevices.filter(d => d.deviceType === 'PC' && d.isOnline);
                  const onlineCount = onlinePcs.length;
                  const totalPaired = pairedDevices.filter(d => d.deviceType === 'PC').length;
                  if (onlineCount > 0) {
                    const connType = onlinePcs[0]?.connectionType;
                    const latency = onlinePcs[0]?.latencyMs;
                    return (
                      <View style={{flexDirection: 'row', alignItems: 'center', gap: 4}}>
                        <View style={{width: 7, height: 7, borderRadius: 4, backgroundColor: connType === 'LAN' ? '#10B981' : '#F59E0B'}} />
                        <Text style={{fontSize: 11, fontFamily: font.semibold, color: connType === 'LAN' ? '#10B981' : '#F59E0B'}}>
                          {onlineCount} online{connType ? ` • ${connType}` : ''}{latency ? ` • ${latency}ms` : ''}
                        </Text>
                      </View>
                    );
                  } else if (totalPaired > 0) {
                    return (
                      <View style={{flexDirection: 'row', alignItems: 'center', gap: 4}}>
                        <View style={{width: 7, height: 7, borderRadius: 4, backgroundColor: colors.text.tertiary}} />
                        <Text style={{fontSize: 11, fontFamily: font.medium, color: colors.text.tertiary}}>Connecting...</Text>
                      </View>
                    );
                  }
                  return null;
                })()}
              </View>
            ) : undefined
          }
          rightActions={
            <View style={{flexDirection: 'row', gap: 10}}>
              <TouchableOpacity onPress={() => setIsConnectModalVisible(true)} style={{padding: 10, backgroundColor: colors.type.image + '22', borderRadius: 10}} accessibilityLabel="Connect devices" accessibilityRole="button">
                <Ionicons name="link" size={20} color={colors.type.image} />
              </TouchableOpacity>
              <TouchableOpacity onPress={() => setGlobalSyncEnabled(!isGlobalSyncEnabled)} style={{padding: 10, backgroundColor: isGlobalSyncEnabled ? colors.accent.successDim : colors.bg.cardHover, borderRadius: 10, borderWidth: 1, borderColor: isGlobalSyncEnabled ? colors.accent.success + '55' : 'transparent'}} accessibilityLabel={isGlobalSyncEnabled ? 'Disable cloud sync' : 'Enable cloud sync'} accessibilityRole="button">
                <Ionicons name={isGlobalSyncEnabled ? 'cloud' : 'cloud-outline'} size={20} color={isGlobalSyncEnabled ? colors.accent.success : colors.text.tertiary} />
              </TouchableOpacity>
              <TouchableOpacity onPress={clearAllClips} style={{padding: 10, backgroundColor: colors.bg.cardHover, borderRadius: 10}} accessibilityLabel="Clear all clips" accessibilityRole="button"><Ionicons name="trash-outline" size={20} color={colors.accent.error} /></TouchableOpacity>
            </View>
          }
        />

        {/* ── Search + Category Filter Bar ── */}
        <View style={{ flexDirection: 'row', alignItems: 'center', paddingHorizontal: 16, marginBottom: 8, gap: 6 }}>
          {/* Search Bar — 1/3 width */}
          <View style={{
            flex: 1,
            flexDirection: 'row',
            alignItems: 'center',
            backgroundColor: colors.bg.input,
            borderRadius: radius.md,
            borderWidth: 1,
            borderColor: feedSearch ? colors.border.accent : colors.border.subtle,
            paddingHorizontal: 10,
            height: 36,
          }}>
            <Ionicons name="search" size={14} color={colors.text.tertiary} />
            <TextInput
              style={{
                flex: 1,
                color: colors.text.primary,
                fontFamily: font.regular,
                fontSize: 13,
                marginLeft: 6,
                padding: 0,
              }}
              placeholder="Search..."
              placeholderTextColor={colors.text.tertiary}
              value={feedSearch}
              onChangeText={setFeedSearch}
              autoCorrect={false}
              returnKeyType="search"
              accessibilityLabel="Search clips"
              accessibilityRole="search"
            />
            {feedSearch.length > 0 && (
              <TouchableOpacity onPress={() => setFeedSearch('')} hitSlop={{ top: 10, bottom: 10, left: 10, right: 10 }} accessibilityLabel="Clear feed search" accessibilityRole="button">
                <Ionicons name="close-circle" size={16} color={colors.text.tertiary} />
              </TouchableOpacity>
            )}
          </View>
          {/* Category chips */}
          {(['All', 'Text', 'Images', 'Docs'] as FeedCategory[]).map(cat => {
            const isActive = feedCategory === cat;
            const chipColors: Record<FeedCategory, { bg: string; text: string; activeBg: string }> = {
              'All':    { bg: 'transparent', text: colors.text.secondary, activeBg: colors.accent.primaryDim },
              'Text':   { bg: 'transparent', text: colors.text.secondary, activeBg: 'rgba(139,146,160,0.15)' },
              'Images': { bg: 'transparent', text: colors.text.secondary, activeBg: 'rgba(167,139,250,0.15)' },
              'Docs':   { bg: 'transparent', text: colors.text.secondary, activeBg: 'rgba(248,113,113,0.15)' },
            };
            const activeTextColors: Record<FeedCategory, string> = {
              'All': colors.accent.primary, 'Text': colors.text.primary, 'Images': '#A78BFA', 'Docs': '#F87171',
            };
            const icons: Record<FeedCategory, string> = { 'All': '🌐', 'Text': '📝', 'Images': '🖼', 'Docs': '📄' };
            return (
              <TouchableOpacity
                key={cat}
                onPress={() => setFeedCategory(cat)}
                accessibilityLabel={`Filter by ${cat}`}
                accessibilityRole="button"
                style={{
                  paddingHorizontal: 10,
                  paddingVertical: 7,
                  borderRadius: radius.sm,
                  backgroundColor: isActive ? chipColors[cat].activeBg : chipColors[cat].bg,
                  borderWidth: 1,
                  borderColor: isActive ? (cat === 'All' ? colors.accent.primary + '44' : activeTextColors[cat] + '44') : colors.border.subtle,
                }}
              >
                <Text style={{
                  fontFamily: isActive ? font.semibold : font.medium,
                  fontSize: 12,
                  color: isActive ? activeTextColors[cat] : chipColors[cat].text,
                }}>{icons[cat]} {cat}</Text>
              </TouchableOpacity>
            );
          })}
        </View>

        {/* Upload Progress Banner (C-1) */}
        {uploadProgress && (
          <View style={{ flexDirection: 'row', alignItems: 'center', paddingHorizontal: 16, paddingVertical: 8, backgroundColor: 'rgba(99,132,255,0.08)', borderRadius: 12, marginHorizontal: 12, marginBottom: 6 }}>
            <ActivityIndicator size="small" color={colors.accent.primary} />
            <View style={{ flex: 1, marginLeft: 10 }}>
              <Text style={{ color: colors.text.primary, fontSize: 13, fontFamily: font.medium }} numberOfLines={1}>📤 Sending {uploadProgress.name}</Text>
              <View style={{ height: 3, backgroundColor: colors.bg.input, borderRadius: 2, marginTop: 4 }}>
                <View style={{ height: 3, backgroundColor: colors.accent.primary, borderRadius: 2, width: `${Math.round(uploadProgress.progress * 100)}%` }} />
              </View>
            </View>
            <Text style={{ color: colors.accent.primary, fontSize: 12, fontFamily: font.bold, marginLeft: 8 }}>{Math.round(uploadProgress.progress * 100)}%{uploadProgress.speedMBps != null ? ` • ${uploadProgress.speedMBps.toFixed(1)} MB/s` : ''}</Text>
          </View>
        )}

        {/* Clip Feed */}
        <View style={styles.feedContainer}>
          {filteredClips.length === 0 && hasLoadedOnceRef.current ? (
            <Text style={styles.emptyText}>{feedSearch ? `No results for "${feedSearch}"` : feedCategory !== 'All' ? `No ${feedCategory.toLowerCase()} items yet.` : 'No clips synced yet.'}</Text>
          ) : filteredClips.length === 0 ? null : (
            // @ts-ignore
            <FlashListCast
              ref={feedListRef}
              data={filteredClips}
              keyExtractor={(item: any, index: number) => item.id ? item.id : index.toString()}
              showsVerticalScrollIndicator={false}
              drawDistance={300}
              estimatedItemSize={120}
              contentContainerStyle={{ paddingBottom: 110 }}

              renderItem={renderClipItem}
            />
          )}
        </View>

        {/* Multi-Select Bar */}
        {isMultiSelectMode && (
          <View style={{backgroundColor: colors.bg.card, borderTopWidth: 1, borderColor: colors.bg.cardHover, padding: 12, flexDirection: 'row', alignItems: 'center', gap: 8}}>
            <Text style={{color: colors.text.secondary, fontSize: 13, fontWeight: '600', marginRight: 4}}>{selectedItemIds.size} selected</Text>
            {(() => { const sel = getSelectedClips(); const allPdf = sel.length >= 2 && sel.every(c => c.Type === 'Pdf' || (c.Title || '').toLowerCase().endsWith('.pdf')); if (allPdf) return (<TouchableOpacity style={{backgroundColor: colors.accent.error, paddingHorizontal: 14, paddingVertical: 8, borderRadius: 10, flexDirection: 'row', alignItems: 'center', gap: 4}} onPress={openMergeModal}><Ionicons name="copy-outline" size={14} color="#FFF" /><Text style={{color: '#FFF', fontSize: 12, fontWeight: '700'}}>Merge PDFs</Text></TouchableOpacity>); return null; })()}
            <TouchableOpacity style={{backgroundColor: colors.accent.success, paddingHorizontal: 14, paddingVertical: 8, borderRadius: 10, flexDirection: 'row', alignItems: 'center', gap: 4}} onPress={async () => {
              try { const selected = clips.filter(c => selectedItemIds.has(c.id || '')); if (selected.length === 0) return; const item = selected[0]; const mUrl = getMediaUrlForItem(item);
              if (mUrl.startsWith('http')) { const safeName = (item.Title || `file_${Date.now()}`).replace(/[^a-zA-Z0-9.-]/g, '_'); const localUri = DOWNLOAD_BASE + safeName; const fileInfo = await FileSystem.getInfoAsync(localUri); let uri = localUri; if (!fileInfo.exists) { if (Platform.OS === 'android') ToastAndroid.show('Downloading for share...', ToastAndroid.SHORT); const dl = await FileSystem.downloadAsync(mUrl, localUri, { headers: { 'X-FlyShelf-Client': 'MobileCompanion', 'X-Pairing-Key': pairingKeyRef.current || '' } }); uri = dl.uri; } await Sharing.shareAsync(uri, { dialogTitle: `Share ${safeName}` }); } else { const text = item.Raw || item.Title || ''; await Sharing.shareAsync(text, { dialogTitle: 'Share' }).catch(() => { Clipboard.setStringAsync(text); if (Platform.OS === 'android') ToastAndroid.show('Copied', ToastAndroid.SHORT); }); }
              } catch(e) { if (Platform.OS === 'android') ToastAndroid.show('Share failed', ToastAndroid.SHORT); }
            }}><Ionicons name="share-outline" size={14} color="#FFF" /><Text style={{color: '#FFF', fontSize: 12, fontWeight: '700'}}>Share</Text></TouchableOpacity>
            <TouchableOpacity style={{backgroundColor: colors.accent.primary, paddingHorizontal: 14, paddingVertical: 8, borderRadius: 10, flexDirection: 'row', alignItems: 'center', gap: 4}} onPress={openForceSyncModal}><Ionicons name="flash" size={14} color="#FFF" /><Text style={{color: '#FFF', fontSize: 12, fontWeight: '700'}}>Force Sync</Text></TouchableOpacity>
            <View style={{flex: 1}} />
            <TouchableOpacity style={{backgroundColor: colors.bg.cardHover, paddingHorizontal: 14, paddingVertical: 8, borderRadius: 10}} onPress={exitMultiSelect}><Text style={{color: '#FFF', fontSize: 12, fontWeight: '700'}}>Cancel</Text></TouchableOpacity>
          </View>
        )}

        {/* PDF Merge Modal */}
        <Modal visible={isMergeModalVisible} animationType="slide" transparent={true}>
          <View style={styles.modalOverlay}><View style={[styles.modalContent, {maxHeight: '80%'}]}>
            <Text style={styles.modalTitle}>Arrange & Merge PDFs</Text>
            <Text style={styles.modalSubtitle}>Drag items up/down to reorder before merging.</Text>
            <ScrollView style={{maxHeight: 350, marginTop: 12}}>
              {mergeQueue.map((item, idx) => (
                <View key={idx} style={{flexDirection: 'row', alignItems: 'center', backgroundColor: colors.bg.cardHover, borderRadius: 12, padding: 12, marginBottom: 8}}>
                  <View style={{width: 28, height: 28, borderRadius: 14, backgroundColor: colors.accent.error, alignItems: 'center', justifyContent: 'center', marginRight: 10}}><Text style={{color: '#FFF', fontSize: 12, fontWeight: '800'}}>{idx + 1}</Text></View>
                  <Text style={{color: '#FFF', fontSize: 13, flex: 1, fontWeight: '500'}} numberOfLines={1}>{item.Title}</Text>
                  <View style={{flexDirection: 'row', gap: 6}}>
                    <TouchableOpacity onPress={() => moveMergeItem(idx, idx - 1)} style={{backgroundColor: colors.bg.card, width: 30, height: 30, borderRadius: 8, alignItems: 'center', justifyContent: 'center'}}><Ionicons name="chevron-up" size={14} color="#FFF" /></TouchableOpacity>
                    <TouchableOpacity onPress={() => moveMergeItem(idx, idx + 1)} style={{backgroundColor: colors.bg.card, width: 30, height: 30, borderRadius: 8, alignItems: 'center', justifyContent: 'center'}}><Ionicons name="chevron-down" size={14} color="#FFF" /></TouchableOpacity>
                  </View>
                </View>
              ))}
            </ScrollView>
            <TouchableOpacity style={{backgroundColor: colors.accent.error, paddingVertical: 16, borderRadius: 14, alignItems: 'center', marginTop: 16}} onPress={executePdfMerge} accessibilityLabel={`Merge ${mergeQueue.length} PDFs`} accessibilityRole="button"><Text style={{color: '#FFF', fontSize: 16, fontWeight: '800'}}>Merge {mergeQueue.length} PDFs</Text></TouchableOpacity>
            <TouchableOpacity style={{backgroundColor: colors.bg.cardHover, paddingVertical: 14, borderRadius: 14, alignItems: 'center', marginTop: 8}} onPress={() => setIsMergeModalVisible(false)} accessibilityLabel="Cancel merge" accessibilityRole="button"><Text style={{color: '#FFF', fontSize: 14, fontWeight: '600'}}>Cancel</Text></TouchableOpacity>
          </View></View>
        </Modal>

        {/* PDF Page Editor */}
        <PdfPageEditor
          visible={pageEditorVisible}
          onClose={closePageEditor}
          pdfUri={pageEditorUri}
          pdfTitle={pageEditorTitle}
          outputDir={CONVERTED_BASE}
          onSaved={(newUri, title) => {
            const newItem: ClipItem = {
              Title: title, Type: 'Pdf', Raw: newUri,
              Time: new Date().toLocaleString(), SourceDeviceName: deviceName || 'Phone',
              SourceDeviceType: 'Mobile', Timestamp: NetworkClock.now(), CachedUri: newUri,
              _receivedVia: 'Local',
            };
            setClips(prev => [newItem, ...prev]);
            scrollToTop();
          }}
        />

        {/* Force Sync Modal */}
        <Modal visible={isForceSyncModalVisible} animationType="slide" transparent={true}>
          <View style={styles.modalOverlay}><View style={[styles.modalContent, {maxHeight: '80%'}]}>
            <Text style={styles.modalTitle}>⚡ Force Sync</Text>
            <Text style={styles.modalSubtitle}>Push {selectedItemIds.size} items to selected devices.</Text>
            <TouchableOpacity style={{backgroundColor: colors.accent.primary, paddingVertical: 14, borderRadius: 14, alignItems: 'center', marginTop: 12, flexDirection: 'row', justifyContent: 'center', gap: 6}} onPress={() => executeForcedSync(forceSyncDevices.map(d => d.key))} accessibilityLabel={`Force sync to all ${forceSyncDevices.length} devices`} accessibilityRole="button"><Ionicons name="flash" size={16} color="#FFF" /><Text style={{color: '#FFF', fontSize: 15, fontWeight: '800'}}>Force to ALL ({forceSyncDevices.length})</Text></TouchableOpacity>
            <Text style={{color: colors.text.secondary, fontSize: 12, marginTop: 16, marginBottom: 8, fontWeight: '700', textTransform: 'uppercase'}}>Or Select Individual Devices</Text>
            <ScrollView style={{maxHeight: 250}}>
              {forceSyncDevices.map((device, i) => (
                <TouchableOpacity key={i} style={[styles.targetOption, {marginBottom: 8}]} onPress={() => executeForcedSync([device.key])}>
                  <View style={{width: 10, height: 10, borderRadius: 5, backgroundColor: device.IsOnline ? colors.accent.success : colors.text.tertiary, marginRight: 10}} />
                  <Ionicons name={device.DeviceType === 'PC' ? 'laptop-outline' : 'phone-portrait-outline'} size={22} color={device.IsOnline ? colors.accent.success : colors.text.tertiary} />
                  <View style={{marginLeft: 10, flex: 1}}>
                    <Text style={{color: '#FFF', fontSize: 15, fontWeight: '600'}}>{device.DeviceName || device.key}</Text>
                    <View style={{flexDirection: 'row', alignItems: 'center', gap: 6, marginTop: 2}}>
                      <View style={{backgroundColor: connectionColors[getConnectionType(device, pcLocalIp)] + '22', borderRadius: 4, paddingHorizontal: 6, paddingVertical: 1}}><Text style={{color: connectionColors[getConnectionType(device, pcLocalIp)], fontSize: 10, fontWeight: '700'}}>{getConnectionType(device, pcLocalIp)}</Text></View>
                      <Text style={{color: device.IsOnline ? colors.accent.success : colors.text.secondary, fontSize: 11}}>{device.IsOnline ? 'Online' : 'Offline'}</Text>
                    </View>
                  </View>
                  <Ionicons name="flash" size={16} color={colors.accent.warning} />
                </TouchableOpacity>
              ))}
              {forceSyncDevices.length === 0 && <Text style={{color: colors.text.secondary, textAlign: 'center', marginTop: 20}}>No devices registered yet.</Text>}
            </ScrollView>
            <TouchableOpacity style={{backgroundColor: colors.bg.cardHover, paddingVertical: 14, borderRadius: 14, alignItems: 'center', marginTop: 12}} onPress={() => setIsForceSyncModalVisible(false)} accessibilityLabel="Cancel" accessibilityRole="button"><Text style={{color: '#FFF', fontSize: 14, fontWeight: '600'}}>Cancel</Text></TouchableOpacity>
          </View></View>
        </Modal>

        {/* Input Area */}
        <View style={styles.inputArea}>
          <TouchableOpacity style={styles.attachButton} onPress={pickImageAndSend} disabled={isSending} accessibilityLabel="Attach image" accessibilityRole="button"><Ionicons name="image-outline" size={24} color={colors.text.tertiary} /></TouchableOpacity>
          <TouchableOpacity style={styles.attachButton} onPress={pickFileAndSend} disabled={isSending} accessibilityLabel="Attach file" accessibilityRole="button"><Ionicons name="attach-outline" size={24} color={colors.text.tertiary} /></TouchableOpacity>
          <TouchableOpacity style={styles.attachButton} onPress={() => setIsCameraOptionsVisible(true)} disabled={isSending} accessibilityLabel="Camera options" accessibilityRole="button"><Ionicons name="camera-outline" size={24} color={colors.text.tertiary} /></TouchableOpacity>
          <TextInput style={styles.textInput} placeholder="Type or paste to send to PC..." placeholderTextColor="#4C5361" value={inputText} onChangeText={setInputText} multiline accessibilityLabel="Message to send to PC" accessibilityRole="text" />
          <TouchableOpacity style={styles.sendButton} onPress={sendTextToPc} disabled={isSending || !inputText} accessibilityLabel="Send message" accessibilityRole="button">
            {isSending ? <ActivityIndicator color="#fff" /> : <Ionicons name="arrow-up-circle" size={36} color={inputText ? colors.accent.primary : colors.bg.cardHover} />}
          </TouchableOpacity>
        </View>
      </KeyboardAvoidingView>

      {/* Expanded Image Modal */}
      <Modal visible={!!expandedImage} transparent={true} animationType="fade" onRequestClose={() => setExpandedImage(null)}>
        <View style={{flex: 1, backgroundColor: 'rgba(0,0,0,0.95)', justifyContent: 'center', alignItems: 'center'}}>
          <TouchableOpacity style={{position: 'absolute', top: 60, right: 20, zIndex: 10, padding: 10, backgroundColor: 'rgba(255,255,255,0.15)', borderRadius: 20, width: 44, height: 44, alignItems: 'center', justifyContent: 'center'}} onPress={() => setExpandedImage(null)} accessibilityLabel="Close image" accessibilityRole="button"><Ionicons name="close" size={24} color="#FFF" /></TouchableOpacity>
          {expandedImage && <Image source={{uri: expandedImage, headers: { 'X-FlyShelf-Client': 'MobileCompanion', 'X-Pairing-Key': pairingKeyRef.current || '' }}} style={{width: '100%', height: '80%'}} contentFit="contain" />}
          {expandedImage && (
            <View style={{position: 'absolute', bottom: 50, flexDirection: 'row', gap: 30, zIndex: 10}}>
              <TouchableOpacity style={{backgroundColor: 'rgba(255,255,255,0.15)', borderRadius: 30, width: 60, height: 60, alignItems: 'center', justifyContent: 'center'}} onPress={async () => {
                if (Platform.OS === 'web') return;
                try { const safeName = `image_${Date.now()}.jpg`; const localUri = DOWNLOAD_BASE + safeName; const dl = await FileSystem.downloadAsync(expandedImage, localUri, { headers: { 'X-FlyShelf-Client': 'MobileCompanion', 'X-Pairing-Key': pairingKeyRef.current || '' } }); const perm = await MediaLibrary.requestPermissionsAsync(); if (perm.status === 'granted') { await MediaLibrary.saveToLibraryAsync(dl.uri); if (Platform.OS === 'android') ToastAndroid.show("Saved to Gallery", ToastAndroid.SHORT); } } catch(e: any) { console.warn('Image save failed:', e?.message || e); if (Platform.OS === 'android') ToastAndroid.show('Save failed', ToastAndroid.SHORT); }
              }} accessibilityLabel="Save image to gallery" accessibilityRole="button"><Ionicons name="download-outline" size={26} color="#FFF" /></TouchableOpacity>
              <TouchableOpacity style={{backgroundColor: colors.accent.primary, borderRadius: 30, width: 60, height: 60, alignItems: 'center', justifyContent: 'center'}} onPress={async () => {
                if (Platform.OS === 'web') return;
                try { const safeName = `image_share_${Date.now()}.jpg`; const localUri = SYNC_CACHE_BASE + safeName; const dl = await FileSystem.downloadAsync(expandedImage, localUri, { headers: { 'X-FlyShelf-Client': 'MobileCompanion', 'X-Pairing-Key': pairingKeyRef.current || '' } }); if (await Sharing.isAvailableAsync()) await Sharing.shareAsync(dl.uri); } catch(e: any) { console.warn('Image share failed:', e?.message || e); if (Platform.OS === 'android') ToastAndroid.show('Share failed', ToastAndroid.SHORT); }
              }} accessibilityLabel="Share image" accessibilityRole="button"><Ionicons name="share-outline" size={26} color="#FFF" /></TouchableOpacity>
            </View>
          )}
        </View>
      </Modal>
      <OnboardingWizard visible={showOnboarding} onComplete={() => { setShowOnboarding(false); AsyncStorage.setItem('@flyshelf_onboarding_done', 'true'); }} />
    </View>
    </LinearGradient>
  );
}
