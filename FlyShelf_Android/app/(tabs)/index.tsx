import React, { useState, useEffect, useRef, useCallback, useMemo } from 'react';
import AppErrorBoundary from '../../components/AppErrorBoundary';
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
import EncryptedStorage from '../../utils/EncryptedStorage';
import * as Notifications from 'expo-notifications';
import NetInfo from '@react-native-community/netinfo';
import { toast, useToast } from '../../context/ToastContext';


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
import { useDownloadQueue, DownloadQueueItem } from '../../features/clipboard/useDownloadQueue';
import { useImageSweep } from '../../features/clipboard/useImageSweep';
import NetworkDashboard from '../../components/NetworkDashboard';
import { useFirebaseSync } from '../../features/sync/useFirebaseSync';
import { useDeviceSync } from '../../features/sync/useDeviceSync';
import { useHeavyUpload } from '../../features/sync/useHeavyUpload';
import { usePairingFlow } from '../../features/sync/usePairingFlow';
import { useScreenshotSync } from '../../features/sync/useScreenshotSync';
import { useIsFocused } from '@react-navigation/native';
import { createTimeoutSignal, clearTimeoutSignal } from '../../utils/timeoutSignal';
import { normalizeTextForFingerprint, fuzzyIsMatch } from '../../utils/textNormalize';


const { AdvanceOverlay } = NativeModules;

// Audit Task 1: normalizeTextForFingerprint and createTimeoutSignal/clearTimeoutSignal
// are now imported from canonical utils (see imports above)

// ════════════════════════════════════════════════════════
// MAIN SCREEN
// ════════════════════════════════════════════════════════
function SyncScreenInner() {
  const { colors, shadows } = useAppTheme();
  const styles = useMemo(() => createSyncStyles(colors, shadows), [colors, shadows]);
  const { pcLocalIp, deviceName, setDeviceName, isGlobalSyncEnabled, setGlobalSyncEnabled, isFloatingBallEnabled, addPairedDevice, pairedDevices, updatePairedDeviceLicensing, updateDeviceStatus, pairingKey: contextPairingKey, regeneratePairingKey, getSyncPrefsForDevice, autoSyncTop5 } = useSettings();

  const isPairedPcPro = pairedDevices.some(d => d.deviceType === 'PC' && d.isPro);

  // A-10 fix: detect when this tab is not focused to skip screenshot polling
  const isFocused = useIsFocused();

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
  // ─── New Items Pill (Change 2) ───
  const [newItemCount, setNewItemCount] = useState(0);
  const isScrolledDownRef = useRef(false);
  const appStateRef = useRef<string>('active');
  // Scroll feed to top — only auto-scrolls if user is near top; otherwise increments pill counter
  const scrollToTop = useCallback(() => {
    if (isScrolledDownRef.current) {
      // User is scrolled down — show "New Items" pill instead of jumping
      setNewItemCount(c => c + 1);
    } else {
      setTimeout(() => {
        try { feedListRef.current?.scrollToOffset({ offset: 0, animated: true }); } catch {}
      }, 300);
    }
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

  // A-4 fix: throttle download progress updates to avoid FlashList re-render per byte
  const progressThrottleRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const pendingProgressRef = useRef<Record<string, number>>({});

  // ─── Clip Persistence: Survive app restarts ───
  const CLIPS_STORAGE_KEY = '@flyshelf_clips';
  const MAX_CLIPS_IN_MEMORY = 300; // OOM guard: cap in-memory clips (pinned items always kept)
  const clipPersistTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const clipsInitializedRef = useRef<boolean>(false);

  // Debounced persist: save clips to AsyncStorage & EncryptedStorage 800ms after last change
  const persistClips = useCallback((clipsToSave: ClipItem[]) => {
    if (clipPersistTimerRef.current) clearTimeout(clipPersistTimerRef.current);
    clipPersistTimerRef.current = setTimeout(async () => {
      try {
        // Keep last 50 items max, exclude transient download progress items
        const persistable = clipsToSave.filter(c => !(c as any)._isTransient && c.Type !== '_DownloadProgress');
        const toSave = persistable.slice(0, 50).map(c => ({
          id: c.id, Title: c.Title, Type: c.Type, Raw: c.Raw,
          Time: c.Time, Timestamp: c.Timestamp,
          SourceDeviceName: c.SourceDeviceName, SourceDeviceType: c.SourceDeviceType,
          CachedUri: c.CachedUri || undefined,
          DownloadUrl: (c as any).DownloadUrl || undefined,
          PreviewUrl: (c as any).PreviewUrl || undefined,
          IsPinned: c.IsPinned || undefined,
        }));
        const jsonStr = JSON.stringify(toSave);
        await Promise.all([
          EncryptedStorage.setItem(CLIPS_STORAGE_KEY, jsonStr).catch(() => {}),
          AsyncStorage.setItem(CLIPS_STORAGE_KEY, jsonStr).catch(() => {}),
        ]);
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
    let mounted = true; // A-13: Guard against state updates after unmount
    (async () => {
      try {
        const stored = await EncryptedStorage.getItem(CLIPS_STORAGE_KEY).catch(() => null)
          || await AsyncStorage.getItem(CLIPS_STORAGE_KEY).catch(() => null);
        if (stored && mounted) {
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
          if (validated.length > 0 && mounted) {
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
      if (mounted) {
        clipsInitializedRef.current = true;
        hasLoadedOnceRef.current = true;
      }
    })();
    return () => { mounted = false; }; // A-13: Cleanup
  }, []);

  // ─── Download Queue + markFileDownloaded ───
  // (extracted to features/clipboard/useDownloadQueue — wired below after pairingKeyRef)

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
        // A-11 fix: hash-based fast path to skip filter+dedup if clips haven't changed
        // Stronger hash: include first 5 items to detect mid-list changes
        const clipHash = `${currentClips.length}:${currentClips.slice(0, 5).map(c => `${c.id || ''}:${c.Timestamp || ''}`).join(',')}`;
        if ((lastNativeSyncRef as any)._lastHash === clipHash) return;
        (lastNativeSyncRef as any)._lastHash = clipHash;
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
            const safeMapped = mapped.map(c => ({ ...c, Raw: (c.Raw || '').substring(0, 50000) }));
            try { AdvanceOverlay.syncNativeDB(JSON.stringify(safeMapped)); } catch(e: any) { console.warn('Overlay syncNativeDB: error', e?.message || e); }
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
          setClips(prev => { const next = [newItem, ...prev]; return next.length > MAX_CLIPS_IN_MEMORY ? [...next.filter(c => c.IsPinned), ...next.filter(c => !c.IsPinned)].slice(0, MAX_CLIPS_IN_MEMORY) : next; });
          scrollToTop();
          transmitTextSecurely(copiedText).catch(() => {});
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
  // SINGLE source of truth: handled by useScreenshotSync hook (clipboard check + screenshot detection + upload).
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
  const [connectionInfo, setConnectionInfo] = useState<{ url: string; latencyMs: number; type: 'LAN' | 'Cloud' } | null>(null);
  const [showNetworkDashboard, setShowNetworkDashboard] = useState(false);
  const [downloadedItems, setDownloadedItems] = useState<Set<string>>(new Set());
  // ─── Download Queue (extracted to useDownloadQueue hook) ─────────────────
  const { enqueueDownload, markFileDownloaded } = useDownloadQueue({
    pairingKeyRef,
    deviceName,
    getCachedPcUrl,
    setClips,
    setDownloadedItems,
  });
  // [REMOVED] downloadProgress — was dead state (never read)
  const [incomingTransferProgress, setIncomingTransferProgress] = useState<{[key: string]: number}>({});
  const [cameraPermission, requestCameraPermission] = useCameraPermissions();
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
    EncryptedStorage.getItem('localDeletedIds').then(val => {
      if (val) { try { const arr = JSON.parse(val); setLocalDeletedIds(new Set(arr.slice(-500))); } catch(e) { console.warn('Load localDeletedIds: error', (e as any)?.message || e); } }
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
            toast.info(`Incoming Batch (${batch.urls.length} items)`, `Receiving from ${batch.sender || 'peer device'}...`);
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
                toast.success("Saved to Gallery", `${batch.urls.length} images saved to 'FlyShelf Extractions' album`);
              }
            } catch (e: any) { 
              toast.error("Batch Transfer Incomplete", e?.message || "Storage permission denied or download timed out");
            }
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


  // ─── Firebase Sync (device discovery + clipboard fallback) ─────────────────
  // Extracted to useFirebaseSync: active_devices listener, lazy Firebase fallback,
  // markPcReachable/Unreachable, lastWorkingPcUrlRef, lastSuccessfulPollRef.
  const {
    markPcReachable,
    markPcUnreachable,
    lastWorkingPcUrlRef,
    lastSuccessfulPollRef,
  } = useFirebaseSync({
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
    autoSyncTop5,
  });

  // ─── Background image download sweep (extracted to feature hook) ───────────
  // downloadingRef lives inside useImageSweep
  useImageSweep({
    imageDownloadTrigger,
    clipsStateRef,
    pairingKeyRef,
    lastWorkingPcUrlRef,
    activeDevicesRef,
    getCachedPcUrl,
    isFloatingBallEnabled,
    setClips,
  });

  // ─── Device Self-Registration ───
  useEffect(() => {
    if (!deviceName) return;
    const myDeviceId = `Mobile_${deviceName.replace(/[^a-zA-Z0-9_]/g, '_')}`;
    const pk = pairingKeyRef.current || contextPairingKey;
    if (!pk) return;
    const registerSelf = async () => {
      try {
        await ensureFirebaseAuth();
        const uid = auth?.currentUser?.uid;
        if (uid) {
          await set(ref(database, `members/${pk}/${uid}`), true).catch(() => {});
        }
        await set(ref(database, `active_devices/${pk}/${myDeviceId}`), {
          DeviceId: myDeviceId,
          DeviceName: deviceName,
          DeviceType: 'Mobile',
          IsOnline: true,
          LocalIp: '',
          Timestamp: NetworkClock.now()
        });
        syncLog('HEARTBEAT', `✅ Device presence registered in Firebase: ${deviceName}`);
      } catch(e) {
        syncLog('HEARTBEAT', `Device registration failed: ${(e as any)?.message || e}`);
      }
    };
    registerSelf();
    // Regular 5-minute heartbeat interval
    const heartbeat = setInterval(registerSelf, 300_000);

    // Instant presence heartbeat on AppState foreground transition
    const appStateSub = AppState.addEventListener('change', (state) => {
      if (state === 'active') {
        registerSelf();
      }
    });

    return () => {
      appStateSub.remove();
      clearInterval(heartbeat);
      if (!isFloatingBallEnabled) {
        set(ref(database, `active_devices/${pk}/${myDeviceId}/IsOnline`), false).catch(() => {});
      }
    };
  }, [deviceName, isFloatingBallEnabled, contextPairingKey]);


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
        setClips(prev => prev.filter(c => c.IsPinned));
        toast.info("Clipboard Cleared", "All unpinned clips removed from local memory");
      } catch(e: any) {
        syncLog('WIPE', `clearAllClips failed: ${e?.message || e}`);
      }
    };
    if (Platform.OS === 'web') { if (window.confirm("Delete all unpinned items?")) await executeWipe(); return; }
    Alert.alert("Clear Entire Clipboard", "Delete all unpinned items from the Global Mesh?", [{ text: "Cancel", style: "cancel" }, { text: "Delete All", style: "destructive", onPress: executeWipe }]);
  };

  // ─── Clipboard & Media Foreground Checks ───
  const lastCopiedRef = React.useRef(lastCopiedText);
  useEffect(() => { lastCopiedRef.current = lastCopiedText; }, [lastCopiedText]);

  // ─── Local PC Polling (extracted to useDeviceSync hook) ──────────────────
  useDeviceSync({
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
    normalizeTextForFingerprint,
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
  });

  // ─── Heavy Upload (extracted to useHeavyUpload hook) ──────────────────────
  const { uploadProgress, pendingUploadPayload, setPendingUploadPayload, executeHeavyUpload } = useHeavyUpload({
    deviceName,
    pcLocalIp,
    pairingKeyRef,
    activeDevices,
    lastWorkingPcUrlRef,
    getCachedPcUrl,
    invalidatePcUrlCache: invalidatePcUrlCache,
    isSending,
    setIsSending,
  });

  // ─── Pairing Flow (extracted to usePairingFlow hook) ──────────────────────
  const { executePairing, connectByCode, generateMyPairingCode, handleBarcodeScanned } = usePairingFlow({
    deviceName,
    isGlobalSyncEnabled,
    setGlobalSyncEnabled,
    pairingKeyRef,
    cachedPcUrlRef,
    cachedPcUrlTimestampRef,
    pairingTimestampRef,
    addPairedDevice,
    regeneratePairingKey,
    pairedDevices,
    isPairing,
    setIsPairing,
    pairedPcName,
    setPairedPcName,
    myPairingCode,
    setMyPairingCode,
    pairingCodeInput,
    setPairingCodeInput,
    isQRScannerActive,
    setIsQRScannerActive,
    isConnectModalVisible,
    setIsConnectModalVisible,
  });

  // ─── Screenshot Sync + Clipboard Foreground Check ──────────────────────────
  // (extracted to useScreenshotSync hook — called after transmitTextSecurely definition below)

  // ─── Network Switch Auto-Recovery (LAN <-> Cloudflare Auto-Switch) ───
  useEffect(() => {
    const netInfoUnsubscribe = NetInfo.addEventListener(state => {
      if (state.isConnected && state.type === 'wifi') {
        syncLog('NET', 'WiFi connected — invalidating cache for instant LAN discovery');
        invalidatePcUrlCache();
        lastWorkingPcUrlRef.current = null;
        cachedPcUrlRef.current = null;
      } else if (state.isConnected && state.type === 'cellular') {
        syncLog('NET', 'Cellular connected (Outside Home) — switching instantly to Cloudflare tunnel');
        invalidatePcUrlCache();
        lastWorkingPcUrlRef.current = null;
        cachedPcUrlRef.current = null;
      } else if (!state.isConnected) {
        setConnectionInfo(null);
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
              if (normCurrent !== normLatest) {
                await Clipboard.setStringAsync(latest.Raw);
                setLastCopiedText(latest.Raw);
                lastCopiedRef.current = latest.Raw;
                toast.clipboard(`Synced from ${latest.SourceDeviceName || 'PC'}`, latest.Raw);
              }
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
                toast.clipboard(`Image Synced from ${latest.SourceDeviceName || 'PC'}`, "Image ready to paste");
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
                (dp) => {
                  const pct = dp.totalBytesExpectedToWrite > 0 ? dp.totalBytesWritten / dp.totalBytesExpectedToWrite : 0;
                  // A-4 fix: throttle progress updates to every 500ms to avoid FlashList churn
                  pendingProgressRef.current[transferId] = pct;
                  if (!progressThrottleRef.current) {
                    progressThrottleRef.current = setTimeout(() => {
                      progressThrottleRef.current = null;
                      const batch = { ...pendingProgressRef.current };
                      pendingProgressRef.current = {};
                      setIncomingTransferProgress(p => ({ ...p, ...batch }));
                    }, 500);
                  }
                }
              );
              activeResumables.add(resumable);
              let dlResult;
              try { dlResult = await resumable.downloadAsync(); }
              finally { activeResumables.delete(resumable); }
              if (aborted) return; // A-14: Don't update state after cleanup
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

  // BUG FIX #5: Ref-based fingerprint to prevent concurrent duplicate uploads.
  // Without this, native + foreground clipboard observers race and upload the same text twice.
  const lastTransmittedFpRef = useRef<string>('');

  // ─── Send Text ───
  const transmitTextSecurely = async (payloadText: string) => {
    // I-6 fix: read the freshest clips via ref - the closure-captured `clips`
    // can be stale when this is called from AppState/interval callbacks.
    const isDuplicate = clipsStateRef.current.some(c => c.Raw === payloadText || c.Title === payloadText);
    if (isDuplicate) return;
    // BUG FIX #5: Ref-based dedup — reject if same content was just transmitted
    const fp = payloadText.substring(0, 200);
    if (fp === lastTransmittedFpRef.current) return;
    lastTransmittedFpRef.current = fp;
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
      let activeUrl = targetUrl;
      if (!activeUrl || !activeUrl.startsWith('http')) {
        activeUrl = await getCachedPcUrl().catch(() => '');
      }

      if (activeUrl && activeUrl.startsWith('http')) {
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
          const sendTimeout = activeUrl.includes('trycloudflare.com') ? 8000 : 3000;
          const response = await fetchWithTimeout(`${activeUrl}/api/sync_text`, { method: 'POST', headers: hdrs, body: jsonBody }, sendTimeout);
          localSuccess = response.ok;

          // If direct send failed with stale URL, invalidate cache, re-resolve and retry once
          if (!localSuccess) {
            invalidatePcUrlCache();
            const freshUrl = await getCachedPcUrl().catch(() => '');
            if (freshUrl && freshUrl !== activeUrl && freshUrl.startsWith('http')) {
              const retryTimeout = freshUrl.includes('trycloudflare.com') ? 8000 : 3000;
              const retryRes = await fetchWithTimeout(`${freshUrl}/api/sync_text`, { method: 'POST', headers: hdrs, body: jsonBody }, retryTimeout);
              localSuccess = retryRes.ok;
            }
          }

          if (localSuccess) {
            if (activeUrl.includes('trycloudflare.com')) {
              toast.syncCloud('✓ Delivered to PC', undefined, '☁️ Cloud');
            } else {
              toast.syncLan('✓ Delivered to PC', undefined, '⚡ LAN');
            }
          }
        } catch(e) {
          // Retry once with freshly resolved URL
          try {
            invalidatePcUrlCache();
            const freshUrl = await getCachedPcUrl().catch(() => '');
            if (freshUrl && freshUrl.startsWith('http')) {
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
              const retryRes = await fetchWithTimeout(`${freshUrl}/api/sync_text`, { method: 'POST', headers: hdrs, body: jsonBody }, 5000);
              localSuccess = retryRes.ok;
              if (localSuccess) {
                if (freshUrl.includes('trycloudflare.com')) {
                  toast.syncCloud('✓ Delivered to PC', undefined, '☁️ Cloud');
                } else {
                  toast.syncLan('✓ Delivered to PC', undefined, '⚡ LAN');
                }
              }
            }
          } catch {}
        }
      }

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
        const next = [sentItem, ...prev];
        return next.length > MAX_CLIPS_IN_MEMORY ? [...next.filter(c => c.IsPinned), ...next.filter(c => !c.IsPinned)].slice(0, MAX_CLIPS_IN_MEMORY) : next;
      });

      if (!localSuccess) {
        toast.warning('PC Offline — Saved Locally', 'Clip saved to feed. Will sync automatically when PC connects.');
      }
    } catch (e) { syncLog('SYNC', `Text transmit error: ${(e as any)?.message || e}`); }
    setIsSending(false);
  };

  // A-1 fix: stabilise transmitTextSecurely via ref to prevent useScreenshotSync interval churn
  const transmitTextSecurelyRef = useRef(transmitTextSecurely);
  useEffect(() => { transmitTextSecurelyRef.current = transmitTextSecurely; });

  // ─── Screenshot Sync + Clipboard Foreground Check (extracted to useScreenshotSync hook) ───
  useScreenshotSync({
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
    transmitTextSecurely: useCallback((text: string) => transmitTextSecurelyRef.current(text), []),
    lastCopiedRef,
    setLastCopiedText,
    normalizeTextForFingerprint,
    MAX_CLIPS_IN_MEMORY,
    isFocused,
  });

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
      toast.info('Merging Files...', `Combining ${mergeQueue.length} files into PDF natively`);

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
        toast.success('Files Merged Successfully', 'Created unified PDF document');
        await Sharing.shareAsync(outputPath, { mimeType: 'application/pdf', UTI: 'com.adobe.pdf', dialogTitle: 'Merged PDF' });
      } catch (localErr: any) {
        // Fallback: try PC merge
        toast.warning('Trying PC Merge Engine', 'Local canvas busy — dispatching to paired PC...');
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
            toast.success('Files Merged via PC', 'Ready to view or export');
            await Sharing.shareAsync(localUri, { mimeType: 'application/pdf', UTI: 'com.adobe.pdf', dialogTitle: 'Merged PDF' }); 
          } 
        } else toast.error('Merge Failed', 'Paired PC could not process selected files');
      }
    } catch (e: any) { toast.error('Merge Error', e?.message || 'Unexpected error during file merge'); }
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
        
        // Sync directly to PC if reachable
        transmitTextSecurely(cleanUrl).catch(() => {});
        toast.clipboard("URL Sanitized & Copied", "Ad and tracking tags removed");
      } else {
        toast.info("URL Clean", "No tracking tags detected on this link");
      }
    } catch (e: any) {
      toast.error("Sanitization Failed", e?.message || "Could not parse URL");
    }
  };

  const handleConvertImageToPdf = async (item: ClipItem) => {
    try {
      // Guard: only convert actual images, not PDFs or other files
      if (item.Type === 'Pdf' || item.Title?.toLowerCase().endsWith('.pdf')) {
        Alert.alert('Already a PDF', 'This item is already a PDF. Use the Edit button to modify pages.');
        return;
      }
      toast.info('Converting Image to PDF...', 'Generating vector PDF wrapper');
      
      const mediaUrl = getMediaUrlForItem(item);
      const imgUri = item.CachedUri || mediaUrl || item.Raw || '';
      if (!imgUri) {
        toast.error('Image Missing', 'Source image data could not be loaded');
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
      
      toast.success('PDF Created', pdfFileName);
      
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
        const next = [newPdfItem, ...prev];
        return next.length > MAX_CLIPS_IN_MEMORY ? [...next.filter(c => c.IsPinned), ...next.filter(c => !c.IsPinned)].slice(0, MAX_CLIPS_IN_MEMORY) : next;
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
    toast.info('Force Syncing...', `Pushing ${selected.length} items to ${targetDeviceKeys.length} device(s)`);
    try {
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
      toast.success('Direct Sync Complete', `All ${selected.length} items transferred successfully`);
    } catch (e: any) { 
      syncLog('FORCE-SYNC', `ERROR: ${e?.message}`); 
      toast.error('Sync Incomplete', e?.message || 'Connection lost during transfer'); 
    }
    } catch (outerErr: any) { 
      syncLog('FORCE-SYNC', `CRASH: ${outerErr?.message}`); 
      toast.error('Sync Error', outerErr?.message || 'Failed to start force sync'); 
    }
    exitMultiSelect();
  };

  // ─── Active Sync Single Item ───
  const activeSyncSingleItem = async (item: ClipItem) => {
    try {
      toast.info('Syncing to PC...', item.Title || item.Type);
      const isTextOrUrl = item.Type === 'Text' || item.Type === 'Url';
      const pk = pairingKeyRef.current || contextPairingKey;
      let targetUrl = await getCachedPcUrl().catch(() => '');

      // 1. Direct P2P transmit if targetUrl is reachable
      let p2pSuccess = false;
      if (targetUrl && targetUrl.startsWith('http')) {
        try {
          const hdrs: any = {
            'Content-Type': 'application/json',
            'X-FlyShelf-Client': 'MobileCompanion',
            'X-Source-Device': deviceName || 'Mobile',
          };
          if (pk) hdrs['X-Pairing-Key'] = pk;

          if (isTextOrUrl) {
            const body = JSON.stringify({
              type: item.Type,
              title: item.Title || item.Raw || 'Text',
              data: item.Raw || item.Title || '',
              sourceDeviceName: deviceName || 'Mobile',
              sourceDeviceId: `Mobile_${(deviceName || 'Phone').replace(/[^a-zA-Z0-9_]/g, '_')}`,
              timestamp: NetworkClock.now(),
            });
            const sendTimeout = targetUrl.includes('trycloudflare.com') ? 8000 : 3000;
            const res = await fetchWithTimeout(`${targetUrl}/api/sync_text`, { method: 'POST', headers: hdrs, body }, sendTimeout);
            p2pSuccess = res.ok;
          } else {
            // Media/File item: check if cached locally
            let localPath = item.CachedUri || (item.Raw && item.Raw.startsWith('file://') ? item.Raw : '');
            if (!localPath && item.Title) {
              const safeName = item.Title.replace(/[^a-zA-Z0-9._-]/g, '_');
              const subfolder = item.Type === 'Pdf' ? 'PDFs' : item.Type === 'Video' ? 'Videos' : item.Type === 'Audio' ? 'Audio' : 'Documents';
              const candidate = `${DOWNLOAD_BASE}${subfolder}/${safeName}`;
              const exists = await FileSystem.getInfoAsync(candidate);
              if (exists.exists) localPath = candidate;
            }

            if (localPath) {
              const uploadUrl = `${targetUrl}/api/sync_file?name=${encodeURIComponent(item.Title || 'file')}&type=${encodeURIComponent(item.Type)}&sourceDevice=${encodeURIComponent(deviceName || 'Mobile')}`;
              await FileSystem.uploadAsync(uploadUrl, localPath, {
                httpMethod: 'POST',
                uploadType: FileSystem.FileSystemUploadType.BINARY_CONTENT,
                headers: {
                  'X-Original-Date': NetworkClock.now().toString(),
                  'X-FlyShelf-Client': 'MobileCompanion',
                  ...(pk ? { 'X-Pairing-Key': pk } : {}),
                },
              });
              p2pSuccess = true;
            }
          }
        } catch (p2pErr: any) {
          syncLog('ACTIVE-SYNC', `P2P sync error: ${p2pErr?.message || p2pErr}`);
        }
      }

      if (p2pSuccess) {
        if (targetUrl?.includes('trycloudflare.com')) {
          toast.syncCloud('✓ Synced to PC', undefined, '☁️ Cloud');
        } else {
          toast.syncLan('✓ Synced to PC', undefined, '⚡ LAN');
        }
      } else {
        toast.error('PC Unreachable', 'Ensure your PC companion app is active and on the same network');
      }
    } catch (err: any) {
      toast.error('Sync Failed', err?.message || 'Network handshake failed');
    }
  };

  // ─── File/Camera/QR Actions ───
  const sendTextToPc = async () => { if (!inputText.trim()) return; await transmitTextSecurely(inputText); setInputText(''); };
  const pickFileAndSend = async () => {
    try {
      const result = await DocumentPicker.getDocumentAsync({ type: '*/*' });
      if (result.canceled) return;
      const file = result.assets[0];
      const ext = file.name.split('.').pop()?.toLowerCase() || '';
      const mime = (file as any).mimeType || '';
      let assignedType = 'File';
      if (mime.startsWith('image/') || ['jpg','jpeg','png','gif','webp'].includes(ext)) assignedType = 'Image';
      else if (mime === 'application/pdf' || ext === 'pdf') assignedType = 'Pdf';
      else if (mime.startsWith('video/') || ['mp4','avi','mkv'].includes(ext)) assignedType = 'Video';
      else if (mime.startsWith('audio/') || ['mp3','wav','aac','flac'].includes(ext)) assignedType = 'Audio';
      else if (mime.includes('presentation') || ['ppt','pptx'].includes(ext)) assignedType = 'Presentation';
      else if (mime.includes('zip') || mime.includes('rar') || mime.includes('compressed') || ['apk','zip','rar','7z'].includes(ext)) assignedType = 'Archive';
      else if (mime.includes('word') || mime.includes('document') || ['doc','docx','txt','rtf'].includes(ext)) assignedType = 'Document';
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
        try { 
          const b64 = await FileSystem.readAsStringAsync(file.uri, { encoding: (FileSystem as any).EncodingType.Base64 }); 
          await Clipboard.setImageAsync(b64); 
          toast.clipboard("Photo Copied to Clipboard", "Ready to paste or send");
        } catch (e) { console.warn('Camera capture clipboard copy: error', (e as any)?.message || e); }
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
        try { if (file.type === 'image') { const b64 = await FileSystem.readAsStringAsync(file.uri, { encoding: (FileSystem as any).EncodingType.Base64 }); await Clipboard.setImageAsync(b64); } } catch (e) { console.warn('Image picker clipboard copy: error', (e as any)?.message || e); }
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


  // ─── Clip Visibility Filter (with category + search) ───
  const clipFilter = (c: ClipItem) => {
    // File types (PDF, Document, etc.): always show if they have a Title — they are continuously re-synced from PC
    if (['Pdf', 'Document', 'File', 'Video', 'Audio', 'Archive', 'Presentation'].includes(c.Type) && c.Title) {
      // Respect wipe timestamp
      if (!((c.Timestamp || 0) >= localWipeTimestamp || c.IsPinned)) return false;
      // Respect local deletion — this was previously missing, causing delete button to not work for files
      if (c.id && localDeletedIds.has(c.id)) return false;
    } else {
      const isVisible = (c.IsPinned || (c.Timestamp || 0) >= localWipeTimestamp) && (!c.id || !localDeletedIds.has(c.id)) && (c.Raw || c.Title);
      if (!isVisible) return false;
    }
    // Filter out Windows file paths (useless on Android) — allow Android file:// URIs
    const rawStr = c.Raw || '';
    if (/^[A-Z]:\\/.test(rawStr)) return false;
    if (rawStr.startsWith('file:///') && /^file:\/\/\/[A-Z]:/.test(rawStr)) return false;
    // Filter out stale download progress cards
    if ((rawStr.startsWith('Downloading from ') || rawStr.startsWith('⏳ Downloading from ')) && rawStr.endsWith('...')) return false;
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

    // ── Search filter (AUDIT FIX: fuzzy matching with prefix, stem, Levenshtein) ──
    if (feedSearch.trim()) {
      const q = feedSearch.trim();
      const searchTarget = `${c.Title || ''} ${c.Raw || ''} ${c.Type || ''}`;
      if (!fuzzyIsMatch(q, searchTarget)) return false;
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
            {/* ═══ OPEN — universal for all types ═══ */}
            <TouchableOpacity onPress={async () => {
              try {
                if (item.Type === 'Image' || item.Type === 'ImageLink') {
                  const imgUri = mediaUrl || item.CachedUri || item.Raw || '';
                  if (imgUri) setExpandedImage(imgUri);
                } else if (item.Type === 'Url' || (item.Raw && item.Raw.startsWith('http'))) {
                  Linking.openURL(item.Raw).catch(() => {});
                } else if (['Pdf', 'Document', 'File', 'Video', 'Audio', 'Archive', 'Presentation'].includes(item.Type)) {
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
                      data: contentUri, flags: 1, type: mimeType,
                    });
                  } else {
                    toast.info('Downloading File...', 'Fetching complete document before opening');
                  }
                } else {
                  // Text: search on Google
                  const query = item.Raw || item.Title || '';
                  if (query) Linking.openURL(`https://www.google.com/search?q=${encodeURIComponent(query)}`).catch(() => {});
                }
              } catch (e: any) {
                toast.error('Cannot Open File', e?.message || 'Unsupported file format or missing viewer app');
              }
              setActiveOptionsId(null);
            }} style={[styles.actionBtnIcon, {backgroundColor: '#0EA5E933'}]}>
              <Ionicons name="open-outline" size={18} color={colors.accent.info} />
            </TouchableOpacity>

            {/* ═══ COPY — universal for all types ═══ */}
            <TouchableOpacity onPress={async () => {
              const contentStr = item.Raw || item.Title || '';
              if (item.Type === 'Image' || item.Type === 'ImageLink') {
                try { 
                  const src = item.CachedUri || mediaUrl || item.Raw; 
                  if (src) { 
                    if (src.startsWith('file://') || src.startsWith('/')) { 
                      const b64 = await FileSystem.readAsStringAsync(src.startsWith('file://') ? src : `file://${src}`, { encoding: FileSystem.EncodingType.Base64 }); 
                      await Clipboard.setImageAsync(b64); 
                    } else { 
                      const localUri = `${SYNC_CACHE_BASE}copy_${Date.now()}.png`; 
                      const dl = await FileSystem.downloadAsync(src, localUri, { headers: { 'X-FlyShelf-Client': 'MobileCompanion', 'X-Pairing-Key': pairingKeyRef.current || '' } }); 
                      const b64 = await FileSystem.readAsStringAsync(dl.uri, { encoding: FileSystem.EncodingType.Base64 }); 
                      await Clipboard.setImageAsync(b64); 
                      try { await FileSystem.deleteAsync(localUri, { idempotent: true }); } catch {} 
                    } 
                    toast.clipboard("Image Copied to Clipboard", "Ready to paste in any app"); 
                  } 
                } catch(e) { 
                  await Clipboard.setStringAsync(contentStr); 
                  toast.clipboard("URL Copied to Clipboard", contentStr); 
                }
              } else { 
                await Clipboard.setStringAsync(contentStr); 
                toast.clipboard("Copied to Clipboard", contentStr); 
              }
              setActiveOptionsId(null);
            }} style={[styles.actionBtnIcon, {backgroundColor: '#4A62EB33'}]}>
              <Ionicons name="copy-outline" size={18} color={colors.accent.primary} />
            </TouchableOpacity>

            {/* ═══ ACTIVE SYNC — universal for all types ═══ */}
            <TouchableOpacity onPress={() => {
              activeSyncSingleItem(item);
              setActiveOptionsId(null);
            }} style={[styles.actionBtnIcon, {backgroundColor: '#10B98133'}]} accessibilityLabel={`Sync ${item.Title || 'item'} to PC`} accessibilityRole="button">
              <Ionicons name="sync-outline" size={18} color="#10B981" />
            </TouchableOpacity>

            {/* ═══ EDIT — PDF only, opens PDF editor tab ═══ */}
            {item.Type === 'Pdf' && (
              <TouchableOpacity onPress={() => { openPageEditor(item.CachedUri || item.Raw, item.Title); setActiveOptionsId(null); }} style={[styles.actionBtnIcon, {backgroundColor: '#F59E0B33'}]}>
                <Ionicons name="create-outline" size={18} color={colors.accent.warning} />
              </TouchableOpacity>
            )}

            {/* ═══ PIN — universal ═══ */}
            <TouchableOpacity onPress={async () => {
              try {
                if (!item.id) {
                  // For local-only items, toggle pin in state
                  setClips(prev => prev.map(c => (c.Title === item.Title && c.Raw === item.Raw) ? {...c, IsPinned: !c.IsPinned} : c));
                  toast.success(item.IsPinned ? "Unpinned" : "Pinned to Top", item.Title || "Clipboard Item");
                } else {
                  await update(ref(database, `${clipboardPath()}/${item.id}`), { IsPinned: !item.IsPinned });
                  setClips(prev => prev.map(c => c.id === item.id ? {...c, IsPinned: !c.IsPinned} : c));
                  toast.success(item.IsPinned ? "Unpinned" : "Pinned to Top", item.Title || "Clipboard Item");
                }
              } catch(e: any) { syncLog('PIN', `Pin/unpin failed: ${e?.message || e}`); }
              setActiveOptionsId(null);
            }} style={[styles.actionBtnIcon, {backgroundColor: item.IsPinned ? '#F59E0B33' : '#2A2F3A'}]}>
              <Ionicons name={item.IsPinned ? "pin" : "pin-outline"} size={18} color={item.IsPinned ? colors.accent.warning : colors.text.tertiary} />
            </TouchableOpacity>

            {/* ═══ DELETE — universal, actually deletes files ═══ */}
            <TouchableOpacity onPress={async () => {
              // Delete cached file from disk
              if (item.CachedUri) {
                await FileSystem.deleteAsync(item.CachedUri, { idempotent: true }).catch(() => {});
              }
              // Also try to delete from known download paths
              if (item.Title && ['Pdf', 'Document', 'File', 'Video', 'Audio', 'Archive', 'Presentation'].includes(item.Type)) {
                const safeName = (item.Title || '').replace(/[^a-zA-Z0-9._-]/g, '_');
                const subfolder = item.Type === 'Pdf' ? 'PDFs' : item.Type === 'Video' ? 'Videos' : item.Type === 'Audio' ? 'Audio' : 'Documents';
                await FileSystem.deleteAsync(`${DOWNLOAD_BASE}${subfolder}/${safeName}`, { idempotent: true }).catch(() => {});
              }
              // Add to localDeletedIds for sync dedup
              if (item.id) {
                setLocalDeletedIds(prev => { const n = new Set(prev); n.add(item.id!); AsyncStorage.setItem('localDeletedIds', JSON.stringify([...n])).catch(() => {}); return n; });
              }
              // Remove from clips array entirely
              setClips(prev => prev.filter(c => {
                if (item.id && c.id === item.id) return false;
                if (c.Title === item.Title && c.Raw === item.Raw && c.Timestamp === item.Timestamp) return false;
                return true;
              }));
              setActiveOptionsId(null);
              toast.info("Item Deleted", "Removed from clip history");
            }} style={[styles.actionBtnIcon, {backgroundColor: '#EF444433'}]}>
              <Ionicons name="trash-outline" size={18} color={colors.accent.error} />
            </TouchableOpacity>
          </View>
        )}
      </View>
    );
  }, [activeOptionsId, isMultiSelectMode, selectedItemIds, incomingTransferProgress, isGlobalSyncEnabled, getMediaUrlForItem, setExpandedImage, openPageEditor]);

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

      {/* QR Scanner — A-17: own error boundary so camera crash doesn't take down entire screen */}
      {isQRScannerActive && (
        <Modal visible={isQRScannerActive} animationType="fade" transparent={false}>
          <View style={{flex: 1, backgroundColor: '#000'}}>
            <AppErrorBoundary fallbackTitle="Camera error">
              <CameraView style={{flex: 1}} facing="back" barcodeScannerSettings={{ barcodeTypes: ["qr"] }} onBarcodeScanned={handleBarcodeScanned} />
            </AppErrorBoundary>
            <TouchableOpacity style={{position: 'absolute', bottom: 50, alignSelf: 'center', backgroundColor: '#EF4444', padding: 15, borderRadius: 30}} onPress={() => { setIsQRScannerActive(false); }} accessibilityLabel="Close QR scanner" accessibilityRole="button">
              <Text style={{color: '#fff', fontWeight: 'bold', fontSize: 16}}>Cancel Scan</Text>
            </TouchableOpacity>
          </View>
        </Modal>
      )}

      <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : undefined} style={{flex: 1}}>
        {/* Header */}
        <ScreenHeader
          title="FlyShelf"
          subtitle={pairingKeyRef.current ? (connectionInfo ? `${pairedPcName || 'PC'}${isPairedPcPro ? ' (Pro)' : ''} ${connectionInfo.type === 'LAN' ? '🟢' : '🟡'} ${connectionInfo.type === 'LAN' ? `LAN${(() => { try { const m = connectionInfo.url.match(/:\/\/([^:/]+)/); return m ? ' ' + m[1] : ''; } catch { return ''; } })()} • ` : 'Cloud • '}${connectionInfo.latencyMs}ms` : (pairedPcName ? `${pairedPcName} — ⏳ Searching...` : '⏳ Searching for PC...')) : '⚠ Not Paired'}
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
              <TouchableOpacity onPress={() => setShowNetworkDashboard(true)} style={{padding: 10, backgroundColor: colors.accent.primaryDim, borderRadius: 10}} accessibilityLabel="Network dashboard" accessibilityRole="button">
                <Ionicons name="pulse-outline" size={20} color={colors.accent.primary} />
              </TouchableOpacity>
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
              onScroll={(e: any) => {
                const offsetY = e?.nativeEvent?.contentOffset?.y || 0;
                isScrolledDownRef.current = offsetY > 200;
                // If user scrolled back to top, reset pill
                if (offsetY < 50 && newItemCount > 0) setNewItemCount(0);
              }}
              scrollEventThrottle={100}
              renderItem={renderClipItem}
            />
          )}

          {/* ═══ New Items Pill (Change 2) ═══ */}
          {newItemCount > 0 && (
            <TouchableOpacity
              onPress={() => {
                setNewItemCount(0);
                isScrolledDownRef.current = false;
                setTimeout(() => {
                  try { feedListRef.current?.scrollToOffset({ offset: 0, animated: true }); } catch {}
                }, 50);
              }}
              activeOpacity={0.85}
              style={{
                position: 'absolute', top: 180, alignSelf: 'center', zIndex: 100,
                backgroundColor: colors.accent.primary, borderRadius: 20,
                paddingHorizontal: 16, paddingVertical: 10,
                flexDirection: 'row', alignItems: 'center', gap: 6,
                shadowColor: '#000', shadowOpacity: 0.35, shadowRadius: 8, shadowOffset: { width: 0, height: 4 },
                elevation: 10,
              }}
            >
              <Ionicons name="arrow-up" size={16} color="#FFF" />
              <Text style={{ color: '#FFF', fontSize: 13, fontWeight: '800' }}>
                {newItemCount} New Item{newItemCount > 1 ? 's' : ''} ↑
              </Text>
            </TouchableOpacity>
          )}
        </View>

        {/* Multi-Select Bar */}
        {isMultiSelectMode && (
          <View style={{backgroundColor: colors.bg.card, borderTopWidth: 1, borderColor: colors.bg.cardHover, padding: 12, flexDirection: 'row', alignItems: 'center', gap: 8}}>
            <Text style={{color: colors.text.secondary, fontSize: 13, fontWeight: '600', marginRight: 4}}>{selectedItemIds.size} selected</Text>
            {(() => { const sel = getSelectedClips(); const allPdf = sel.length >= 2 && sel.every(c => c.Type === 'Pdf' || (c.Title || '').toLowerCase().endsWith('.pdf')); if (allPdf) return (<TouchableOpacity style={{backgroundColor: colors.accent.error, paddingHorizontal: 14, paddingVertical: 8, borderRadius: 10, flexDirection: 'row', alignItems: 'center', gap: 4}} onPress={openMergeModal}><Ionicons name="copy-outline" size={14} color="#FFF" /><Text style={{color: '#FFF', fontSize: 12, fontWeight: '700'}}>Merge PDFs</Text></TouchableOpacity>); return null; })()}
            <TouchableOpacity style={{backgroundColor: colors.accent.success, paddingHorizontal: 14, paddingVertical: 8, borderRadius: 10, flexDirection: 'row', alignItems: 'center', gap: 4}} onPress={async () => {
              try { 
                const selected = clips.filter(c => selectedItemIds.has(c.id || '')); 
                if (selected.length === 0) return; 
                const item = selected[0]; 
                const mUrl = getMediaUrlForItem(item);
                if (mUrl.startsWith('http')) { 
                  const safeName = (item.Title || `file_${Date.now()}`).replace(/[^a-zA-Z0-9.-]/g, '_'); 
                  const localUri = DOWNLOAD_BASE + safeName; 
                  const fileInfo = await FileSystem.getInfoAsync(localUri); 
                  let uri = localUri; 
                  if (!fileInfo.exists) { 
                    toast.info('Downloading File...', `Preparing ${safeName} for sharing`); 
                    const dl = await FileSystem.downloadAsync(mUrl, localUri, { headers: { 'X-FlyShelf-Client': 'MobileCompanion', 'X-Pairing-Key': pairingKeyRef.current || '' } }); 
                    uri = dl.uri; 
                  } 
                  await Sharing.shareAsync(uri, { dialogTitle: `Share ${safeName}` }); 
                } else { 
                  const text = item.Raw || item.Title || ''; 
                  await Sharing.shareAsync(text, { dialogTitle: 'Share' }).catch(() => { 
                    Clipboard.setStringAsync(text); 
                    toast.clipboard('Copied to Clipboard', text); 
                  }); 
                }
              } catch(e: any) { 
                toast.error('Share Failed', e?.message || 'Could not export selected items'); 
              }
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
            setClips(prev => { const next = [newItem, ...prev]; return next.length > MAX_CLIPS_IN_MEMORY ? [...next.filter(c => c.IsPinned), ...next.filter(c => !c.IsPinned)].slice(0, MAX_CLIPS_IN_MEMORY) : next; });
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
                try { 
                  const safeName = `image_${Date.now()}.jpg`; 
                  const localUri = DOWNLOAD_BASE + safeName; 
                  const dl = await FileSystem.downloadAsync(expandedImage, localUri, { headers: { 'X-FlyShelf-Client': 'MobileCompanion', 'X-Pairing-Key': pairingKeyRef.current || '' } }); 
                  const perm = await MediaLibrary.requestPermissionsAsync(); 
                  if (perm.status === 'granted') { 
                    await MediaLibrary.saveToLibraryAsync(dl.uri); 
                    toast.success("Saved to Gallery", "Photo saved to your Photos library"); 
                  } else {
                    toast.error("Permission Denied", "Enable storage/photo access in settings to save images");
                  }
                } catch(e: any) { 
                  toast.error("Save Failed", e?.message || "Could not save photo to gallery"); 
                }
              }} accessibilityLabel="Save image to gallery" accessibilityRole="button"><Ionicons name="download-outline" size={26} color="#FFF" /></TouchableOpacity>
              <TouchableOpacity style={{backgroundColor: colors.accent.primary, borderRadius: 30, width: 60, height: 60, alignItems: 'center', justifyContent: 'center'}} onPress={async () => {
                if (Platform.OS === 'web') return;
                try { 
                  const safeName = `image_share_${Date.now()}.jpg`; 
                  const localUri = SYNC_CACHE_BASE + safeName; 
                  const dl = await FileSystem.downloadAsync(expandedImage, localUri, { headers: { 'X-FlyShelf-Client': 'MobileCompanion', 'X-Pairing-Key': pairingKeyRef.current || '' } }); 
                  if (await Sharing.isAvailableAsync()) await Sharing.shareAsync(dl.uri); 
                  try { await FileSystem.deleteAsync(dl.uri, { idempotent: true }); } catch {} 
                } catch(e: any) { 
                  toast.error('Share Failed', e?.message || 'Could not export image'); 
                }
              }} accessibilityLabel="Share image" accessibilityRole="button"><Ionicons name="share-outline" size={26} color="#FFF" /></TouchableOpacity>
            </View>
          )}
        </View>
      </Modal>
      <NetworkDashboard visible={showNetworkDashboard} onClose={() => setShowNetworkDashboard(false)} pcUrl={cachedPcUrlRef.current} pairingKey={pairingKeyRef.current || null} />
      <OnboardingWizard visible={showOnboarding} onComplete={() => { setShowOnboarding(false); AsyncStorage.setItem('@flyshelf_onboarding_done', 'true'); }} />
    </View>
    </LinearGradient>
  );
}

export default function SyncScreen() {
  return (
    <AppErrorBoundary fallbackTitle="Sync screen crashed">
      <SyncScreenInner />
    </AppErrorBoundary>
  );
}
