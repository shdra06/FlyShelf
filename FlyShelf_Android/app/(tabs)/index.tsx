import React, { useState, useEffect, useRef, useCallback, useMemo } from 'react';
import AppErrorBoundary from '../../components/AppErrorBoundary';
import { View, Text, TextInput, TouchableOpacity, ActivityIndicator, KeyboardAvoidingView, Platform, Alert, AppState, Modal, NativeModules, ScrollView, Share, RefreshControl, Animated, StyleSheet, FlatList } from 'react-native';
// SafeAreaView removed — ScreenHeader handles safe area
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { LinearGradient } from 'expo-linear-gradient';
import * as Sharing from 'expo-sharing';
import * as IntentLauncher from 'expo-intent-launcher';
import { useSettings } from '../../context/SettingsContext';
import { Ionicons } from '@expo/vector-icons';
import { database, auth, ensureFirebaseAuth } from '../../firebaseConfig';
import { syncLog } from '../../utils/debugLog';
import { ref, set, get, onValue, query, update } from 'firebase/database';
import * as DocumentPicker from 'expo-document-picker';
import * as Clipboard from 'expo-clipboard';
import * as FileSystem from 'expo-file-system/legacy';
import { getSecureItem } from '../../utils/secureStorage';
import * as MediaLibrary from 'expo-media-library';
import { Image } from 'expo-image';


import * as Linking from 'expo-linking';
import * as ImagePicker from 'expo-image-picker';
import { CameraView, useCameraPermissions } from 'expo-camera';
import AsyncStorage from '@react-native-async-storage/async-storage';
import EncryptedStorage from '../../utils/EncryptedStorage';
import NetInfo from '@react-native-community/netinfo';
import { toast } from '../../context/ToastContext';
import * as Haptics from 'expo-haptics';


// ═══ Extracted Modules ═══
import { ClipItem, DOWNLOAD_BASE, SYNC_CACHE_BASE, CONVERTED_BASE } from '../../utils/clipTypes';
import { fetchWithTimeout, getConnectionType, connectionColors, resolveOptimalUrl, getMediaUrl, decryptDeviceList, isValidPairingKey } from '../../utils/networkHelpers';
import { DirectMesh } from '../../utils/directMesh';
import { NetworkClock } from '../../utils/networkClock';
import { createSyncStyles } from '../../styles/syncStyles';
import { font, radius, space } from '../../styles/theme';
import { useAppTheme } from '../../hooks/useAppTheme';
import AnimatedCard from '../../components/AnimatedCard';
import { useSharedValue } from 'react-native-reanimated';
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
import { useDownloadQueue } from '../../features/clipboard/useDownloadQueue';
import { useImageSweep } from '../../features/clipboard/useImageSweep';
import NetworkDashboard from '../../components/NetworkDashboard';
import { useFirebaseSync } from '../../features/sync/useFirebaseSync';
import { useDeviceSync } from '../../features/sync/useDeviceSync';
import { useHeavyUpload } from '../../features/sync/useHeavyUpload';
import { usePairingFlow } from '../../features/sync/usePairingFlow';
import { useScreenshotSync } from '../../features/sync/useScreenshotSync';
import { useIsFocused, useFocusEffect } from '@react-navigation/native';
import { normalizeTextForFingerprint, fuzzyIsMatch } from '../../utils/textNormalize';
import { router } from 'expo-router';
import { useVault } from '../../features/vault/useVault';

// ═══ Home Dashboard Components ═══
import MaterialSearchBar from '../../components/MaterialSearchBar';
import CategoryTile from '../../components/home/CategoryTile';
import QuickActionChips from '../../components/home/QuickActionChips';
import RecentActivityFeed, { ActivityItem } from '../../components/home/RecentActivityFeed';
import HomeFab from '../../components/home/HomeFab';
import SendTextModal from '../../components/home/SendTextModal';
import { createHomeStyles } from '../../styles/homeStyles';


const { AdvanceOverlay } = NativeModules;

// Audit Task 1: normalizeTextForFingerprint and createTimeoutSignal/clearTimeoutSignal
// are now imported from canonical utils (see imports above)

const SkeletonLoader = () => {
  const { colors } = useAppTheme();
  const opacity = useRef(new Animated.Value(0.3)).current;

  useEffect(() => {
    const anim = Animated.loop(
      Animated.sequence([
        Animated.timing(opacity, { toValue: 0.7, duration: 800, useNativeDriver: true }),
        Animated.timing(opacity, { toValue: 0.3, duration: 800, useNativeDriver: true })
      ])
    );
    anim.start();
    return () => anim.stop();
  }, []);

  return (
    <View style={{ padding: 16 }}>
      {[1, 2, 3, 4, 5].map(i => (
        <Animated.View key={i} style={{ 
          height: 80, 
          backgroundColor: colors.border.subtle, 
          borderRadius: 12, 
          marginBottom: 12, 
          opacity 
        }} />
      ))}
    </View>
  );
};

// ════════════════════════════════════════════════════════
// MAIN SCREEN
// ════════════════════════════════════════════════════════
function SyncScreenInner() {
  const { colors, shadows, isDark } = useAppTheme();
  const styles = useMemo(() => createSyncStyles(colors, shadows), [colors, shadows]);
  const homeStyles = useMemo(() => createHomeStyles(colors, shadows), [colors, shadows]);
  const { pcLocalIp, deviceName, setDeviceName, isGlobalSyncEnabled, setGlobalSyncEnabled, isFloatingBallEnabled, addPairedDevice, pairedDevices, updatePairedDeviceLicensing, updateDeviceStatus, pairingKey: contextPairingKey, regeneratePairingKey, getSyncPrefsForDevice, autoSyncTop5, isOfflineOutboxEnabled, defaultHomeCard, showBottomHomeSwitcher, isLoading: settingsLoading } = useSettings();

  const isPairedPcPro = pairedDevices.some(d => d.deviceType === 'PC' && d.isPro);

  // ── View Mode: Home Dashboard vs Clipboard Feed ──
  type ViewMode = 'home' | 'clipboard';
  const [viewMode, setViewMode] = useState<ViewMode>('home');
  const insets = useSafeAreaInsets();

  const defaultCardAppliedRef = useRef(false);
  useEffect(() => {
    if (!defaultCardAppliedRef.current && defaultHomeCard && defaultHomeCard !== 'home') {
      defaultCardAppliedRef.current = true;
      if (defaultHomeCard === 'clipboard') {
        setViewMode('clipboard');
      } else if (defaultHomeCard === 'archive') {
        router.push('/(tabs)/archive');
      } else if (defaultHomeCard === 'vault') {
        router.push('/(tabs)/vault' as any);
      } else if (defaultHomeCard === 'notes') {
        router.push('/(tabs)/notes');
      } else if (defaultHomeCard === 'todo') {
        router.push('/(tabs)/todo');
      } else if (defaultHomeCard === 'settings') {
        router.push('/(tabs)/settings');
      }
    }
  }, [defaultHomeCard]);

  // A-10 fix: detect when this tab is not focused to skip screenshot polling
  const isFocused = useIsFocused();

  useEffect(() => {
    if (Platform.OS === 'android' && AdvanceOverlay) {
      try {
        if (typeof AdvanceOverlay.startOverlay === 'function') {
          AdvanceOverlay.startOverlay();
        }
      } catch (e) {}
      try {
        if (typeof AdvanceOverlay.setBallVisible === 'function') {
          AdvanceOverlay.setBallVisible(isFloatingBallEnabled);
        }
      } catch (e) {}
    }
  }, [isFloatingBallEnabled]);

  // ─── Sync Offline Outbox setting to DirectMesh module ───
  useEffect(() => {
    DirectMesh.setOfflineOutboxEnabled(isOfflineOutboxEnabled);
  }, [isOfflineOutboxEnabled]);

  // ─── Ghost Wipe Filter State ───
  const [localWipeTimestamp, setLocalWipeTimestamp] = useState<number>(0);
  const [localDeletedIds, setLocalDeletedIds] = useState<Set<string>>(new Set());

  // ─── Core State ───
  const [isStorageLoaded, setIsStorageLoaded] = useState(false);
  const [clips, setClips] = useState<ClipItem[]>([]);
  // ─── Ref mirror for clips state — used by effects that must READ clips without DEPENDING on clips ───
  const clipsStateRef = useRef<ClipItem[]>([]);
  useEffect(() => { clipsStateRef.current = clips; }, [clips]);
  const feedListRef = useRef<any>(null);

  // ─── Smart Recent Activity Cross-Data State ───
  const { manifest } = useVault();
  const [recentNotes, setRecentNotes] = useState<any[]>([]);
  const [recentTodos, setRecentTodos] = useState<any[]>([]);

  useFocusEffect(
    useCallback(() => {
      let isMounted = true;
      (async () => {
        try {
          let rawNotes = await EncryptedStorage.getItem('@flyshelf_notes');
          if (!rawNotes) rawNotes = await AsyncStorage.getItem('@flyshelf_notes');
          if (rawNotes && isMounted) {
            const parsedNotes = JSON.parse(rawNotes);
            if (Array.isArray(parsedNotes)) setRecentNotes(parsedNotes);
          }
        } catch {}

        try {
          let rawTodos = await EncryptedStorage.getItem('@flyshelf_todos');
          if (!rawTodos) rawTodos = await AsyncStorage.getItem('@flyshelf_todos');
          if (rawTodos && isMounted) {
            const parsedTodos = JSON.parse(rawTodos);
            if (Array.isArray(parsedTodos)) setRecentTodos(parsedTodos);
          }
        } catch {}
      })();
      return () => { isMounted = false; };
    }, [])
  );
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
  const sentContentFingerprintsRef = useRef<Map<string, number>>(new Map());
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
        // Keep last 1000 items max, exclude transient download progress items
        const persistable = clipsToSave.filter(c => !(c as any)._isTransient && c.Type !== '_DownloadProgress');
        const toSave = persistable.slice(0, 1000).map(c => ({
          id: c.id, Title: c.Title, Type: c.Type, Raw: c.Raw,
          Time: c.Time, Timestamp: c.Timestamp,
          SourceDeviceName: c.SourceDeviceName, SourceDeviceType: c.SourceDeviceType,
          CachedUri: c.CachedUri || undefined,
          DownloadUrl: (c as any).DownloadUrl || undefined,
          PreviewUrl: (c as any).PreviewUrl || undefined,
          IsPinned: c.IsPinned || undefined,
        }));
        const jsonStr = JSON.stringify(toSave);
        const diskClipsBackup = `${FileSystem.documentDirectory}flyshelf_clips_backup.json`;
        await Promise.all([
          EncryptedStorage.setItem(CLIPS_STORAGE_KEY, jsonStr).catch(() => {}),
          AsyncStorage.setItem(CLIPS_STORAGE_KEY, jsonStr).catch(() => {}),
          FileSystem.writeAsStringAsync(diskClipsBackup, jsonStr).catch(() => {}),
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
    }).catch(() => {});
  }, []);

  // Load persisted clips on startup + validate CachedUri files
  useEffect(() => {
    let mounted = true; // A-13: Guard against state updates after unmount
    (async () => {
      try {
        const diskClipsBackup = `${FileSystem.documentDirectory}flyshelf_clips_backup.json`;
        let stored = await EncryptedStorage.getItem(CLIPS_STORAGE_KEY).catch(() => null)
          || await AsyncStorage.getItem(CLIPS_STORAGE_KEY).catch(() => null);
        if (!stored) {
          try {
            const bInfo = await FileSystem.getInfoAsync(diskClipsBackup);
            if (bInfo.exists) {
              stored = await FileSystem.readAsStringAsync(diskClipsBackup);
            }
          } catch {}
        }
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
        setIsStorageLoaded(true);
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
    }).catch(() => {});
  }, []);
  // Keep ref in sync when context key changes (e.g. after pairing or regeneration)
  useEffect(() => {
    if (isValidPairingKey(contextPairingKey)) {
      pairingKeyRef.current = contextPairingKey;
      if (Platform.OS === 'android' && AdvanceOverlay?.setPairingKey) AdvanceOverlay.setPairingKey(contextPairingKey);
    }
  }, [contextPairingKey]);
  // ZERO-TRUST: clipboardPath() removed — Firebase stores zero clipboard data.

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
              const safeName = (c.Title || '').replace(/[^a-zA-Z0-9.-]/g, '_');
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
  const overlayTxCountRef = useRef(0); // Track how many overlay items sent to PC this session
  useEffect(() => {
    if (Platform.OS !== 'android' || !AdvanceOverlay || !isFloatingBallEnabled || !deviceName) return;
    // Reset counter on effect restart (e.g. settings change)
    overlayTxCountRef.current = 0;
    // Immediately configure overlay with PC URL for seamless sync
    (async () => {
      try {
        const targetUrl = await getCachedPcUrl();
        if (targetUrl && typeof AdvanceOverlay.setPcUrl === 'function') AdvanceOverlay.setPcUrl(targetUrl);
        if (deviceName && typeof AdvanceOverlay.setDeviceName === 'function') AdvanceOverlay.setDeviceName(deviceName);
      } catch (e) { syncLog('OVERLAY', `Overlay URL config failed: ${(e as any)?.message || e}`); }
    })();
    const OVERLAY_TX_INITIAL_CAP = 3; // Max items sent to PC on initial overlay sync
    const pollInterval = setInterval(async () => {
      try {
        if (typeof AdvanceOverlay.getLastCopiedFromOverlay !== 'function') return;
        const copiedText = await AdvanceOverlay.getLastCopiedFromOverlay();
        if (copiedText && copiedText.trim().length > 0) {
          // Fingerprint to prevent echo back from Firebase
          sentContentFingerprintsRef.current.set(copiedText.substring(0, 200), Date.now());
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
          // LEAKAGE FIX: Only send first 3 items to PC on initial overlay sync.
          // After that, only genuinely NEW user-copied items are transmitted (not echoes of PC items).
          overlayTxCountRef.current++;
          // Check if this item was received FROM PC (echo-back detection)
          const isEchoFromPc = clipsStateRef.current.some(c =>
            c._receivedVia === 'LAN' && (c.Raw === copiedText || c.Title === copiedText)
          );
          if (!isEchoFromPc && overlayTxCountRef.current <= OVERLAY_TX_INITIAL_CAP) {
            transmitTextSecurelyRef.current(copiedText).catch(() => {});
          } else if (!isEchoFromPc && overlayTxCountRef.current > OVERLAY_TX_INITIAL_CAP) {
            // After initial cap, still transmit genuinely new user copies
            transmitTextSecurelyRef.current(copiedText).catch(() => {});
          }
          // Items that ARE echoes from PC are kept local-only — never sent back
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
  const [isRefreshing, setIsRefreshing] = useState(false);
  const onRefresh = useCallback(async () => {
    setIsRefreshing(true);
    if (getCachedPcUrl) {
      await getCachedPcUrl();
    }
    setTimeout(() => {
      setIsRefreshing(false);
    }, 1500);
  }, [getCachedPcUrl]);

  const renderConnectionBanner = () => {
    if (connectionInfo) {
      if (connectionInfo.type === 'LAN') {
        return (
          <View style={{ height: 28, width: '100%', backgroundColor: 'rgba(76, 175, 80, 0.1)', flexDirection: 'row', alignItems: 'center', justifyContent: 'center' }}>
            <View style={{ width: 8, height: 8, borderRadius: 4, backgroundColor: '#4CAF50', marginRight: 6 }} />
            <Text style={{ fontSize: 11, color: colors.text.secondary }}>Connected via LAN</Text>
          </View>
        );
      } else {
        return (
          <View style={{ height: 28, width: '100%', backgroundColor: 'rgba(255, 152, 0, 0.1)', flexDirection: 'row', alignItems: 'center', justifyContent: 'center' }}>
            <View style={{ width: 8, height: 8, borderRadius: 4, backgroundColor: '#FF9800', marginRight: 6 }} />
            <Text style={{ fontSize: 11, color: colors.text.secondary }}>Connected via Cloud</Text>
          </View>
        );
      }
    }
    
    const isPaired = pairedDevices.length > 0;
    if (isPaired) {
      // Check if Firebase sees PC online — show "Reconnecting" not "Offline"
      const firebaseSeesPC = pairedDevices.some(d => d.deviceType === 'PC' && d.isOnline);
      if (isRefreshing || firebaseSeesPC) {
        return (
          <TouchableOpacity onPress={onRefresh} style={{ height: 28, width: '100%', backgroundColor: 'rgba(255, 152, 0, 0.1)', flexDirection: 'row', alignItems: 'center', justifyContent: 'center' }}>
            <ActivityIndicator size={10} color={colors.text.secondary} style={{ marginRight: 6 }} />
            <Text style={{ fontSize: 11, color: colors.text.secondary }}>Reconnecting to PC...</Text>
          </TouchableOpacity>
        );
      }
      return (
        <TouchableOpacity onPress={onRefresh} style={{ height: 28, width: '100%', backgroundColor: 'rgba(244, 67, 54, 0.1)', flexDirection: 'row', alignItems: 'center', justifyContent: 'center' }}>
          <View style={{ width: 8, height: 8, borderRadius: 4, backgroundColor: '#F44336', marginRight: 6 }} />
          <Text style={{ fontSize: 11, color: colors.text.secondary }}>PC Offline • Tap to retry</Text>
        </TouchableOpacity>
      );
    }
    return null;
  };

  const [inputText, setInputText] = useState('');
  const [isSending, setIsSending] = useState(false);
  const [lastCopiedText, setLastCopiedText] = useState('');
  const [setupName, setSetupName] = useState('');
  useEffect(() => { if (deviceName) setSetupName(deviceName); }, [deviceName]);
  const [isSendTextModalVisible, setIsSendTextModalVisible] = useState(false);
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
  const [isTorchOn, setIsTorchOn] = useState(false);
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
      else { setLocalWipeTimestamp(0); AsyncStorage.setItem('localWipeTimestamp', '0').catch(() => {}); }
    }).catch(() => {});
    EncryptedStorage.getItem('localDeletedIds').then(val => {
      if (val) { try { const arr = JSON.parse(val); setLocalDeletedIds(new Set(arr.slice(-500))); } catch(e) { console.warn('Load localDeletedIds: error', (e as any)?.message || e); } }
    }).catch(() => {});
    (async () => {
      if (!(await AsyncStorage.getItem('@flyshelf_onboarding_done'))) setShowOnboarding(true);
    })();
  }, []);

  // ─── Peer Relay: REMOVED for Zero-Trust policy (Firebase must never relay files or execute commands) ───

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
        // Returning to foreground — stop native sync and merge any clips received while backgrounded
        if (Platform.OS === 'android' && AdvanceOverlay) {
          try {
            if (typeof AdvanceOverlay.setSyncEnabled === 'function') {
              AdvanceOverlay.setSyncEnabled(false);
            }
          } catch {}
          if (typeof AdvanceOverlay.getPendingClips === 'function') {
            try {
              const pendingPromise = AdvanceOverlay.getPendingClips();
              if (pendingPromise && typeof pendingPromise.then === 'function') {
                pendingPromise.then((json: string) => {
                  try {
                    const pending = JSON.parse(json || '[]') as Array<{ Raw?: string; Title?: string; Type?: string; SourceDeviceName?: string }>;
                    if (pending.length > 0) {
                      setClips(prev => {
                        const newItems: ClipItem[] = pending
                          .filter(p => p.Raw && p.Raw.trim().length > 0)
                          .map(p => ({
                            Title: (p.Title || (p.Raw || '').substring(0, 80)),
                            Type: p.Type || 'Text',
                            Raw: p.Raw!,
                            Time: new Date().toLocaleString(),
                            SourceDeviceName: p.SourceDeviceName || 'PC',
                            SourceDeviceType: 'Desktop' as const,
                            Timestamp: NetworkClock.now(),
                            _receivedVia: (cachedPcUrlRef.current?.includes('trycloudflare.com') ? 'Cloud' : 'LAN') as 'Cloud' | 'LAN',
                          }));
                        // Dedup: skip items whose Raw already exists at the top of the feed
                        const existingRaws = new Set(prev.slice(0, 20).map(c => c.Raw));
                        const unique = newItems.filter(n => !existingRaws.has(n.Raw));
                        if (unique.length === 0) return prev;
                        return [...unique, ...prev];
                      });
                    }
                  } catch (_) {}
                }).catch(() => {});
              }
            } catch {}
          }
        }
      } else if (state === 'background') {
        // Going to background — enable native sync so the foreground service keeps syncing
        if (Platform.OS === 'android' && AdvanceOverlay && typeof AdvanceOverlay.setSyncEnabled === 'function') {
          try {
            AdvanceOverlay.setSyncEnabled(true);
          } catch {}
        }
      }
    });

    return () => {
      appStateSub.remove();
      clearInterval(heartbeat);
      if (!isGlobalSyncEnabled) {
        set(ref(database, `active_devices/${pk}/${myDeviceId}/IsOnline`), false).catch(() => {});
      }
    };
  }, [deviceName, isGlobalSyncEnabled, contextPairingKey]);


  // ─── Periodic dedup cleanup (every 60s) ───
  useEffect(() => {
    const cleanup = setInterval(() => {
      const now = NetworkClock.now();
      // processedEventsRef cleanup handled by dedicated interval at L106-114 (5min/10min TTL)
      // Clean sentContentFingerprintsRef — TTL eviction, NOT full clear (prevents echo loops)
      // Prune entries older than 1 hour
      const ONE_HOUR = 3600000;
      if (sentContentFingerprintsRef.current.size > 200) {
        for (const [key, timestamp] of sentContentFingerprintsRef.current.entries()) {
          if (now - timestamp > ONE_HOUR) sentContentFingerprintsRef.current.delete(key);
        }
        if (sentContentFingerprintsRef.current.size > 500) {
          // Hard cap: keep only the 200 most recent entries
          const sorted = [...sentContentFingerprintsRef.current.entries()].sort((a, b) => b[1] - a[1]).slice(0, 200);
          sentContentFingerprintsRef.current = new Map(sorted);
        }
        syncLog('CLEANUP', `Pruned sentContentFingerprints to ${sentContentFingerprintsRef.current.size}`);
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
              const safeName = (item.Title || '').replace(/[^a-zA-Z0-9.-]/g, '_');
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
      sentContentFingerprintsRef.current.set(finalRaw.substring(0, 200), Date.now());
      const txEventId = generateEventId();
      processedEventsRef.current.set(txEventId, NetworkClock.now());
      let activeUrl = targetUrl;
      if (!activeUrl || !activeUrl.startsWith('http')) {
        activeUrl = await getCachedPcUrl().catch(() => '');
      }

      const dispatchResult = await DirectMesh.sendClip({
        type: finalType,
        title: payloadText.length > 40 ? payloadText.substring(0, 40) + '...' : payloadText,
        data: finalRaw,
        deviceName: deviceName || 'Mobile',
        activeUrl: activeUrl,
      });

      if (dispatchResult.success) {
        if (dispatchResult.transport === 'ws') {
          toast.syncLan('✓ Delivered via Direct WebSocket', undefined, '⚡ Real-time');
        } else if (dispatchResult.transport === 'cloud') {
          toast.syncCloud('✓ Delivered to PC', undefined, '☁️ Cloud');
        } else {
          toast.syncLan('✓ Delivered to PC', undefined, '⚡ LAN');
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

      if (!dispatchResult.success) {
        if (dispatchResult.transport === 'outbox') {
          toast.warning('PC Offline — Queued', 'Clip saved to outbox. Will sync automatically when PC reconnects.');
        } else {
          toast.warning('PC Offline — Saved Locally', 'Clip saved to feed only. Enable "Offline Queue" in Settings to auto-sync when PC reconnects.');
        }
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
          const decryptedDevs = await decryptDeviceList(rawDevs, pk);
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
                await fetchWithTimeout(`${url}/api/sync`, {
                  method: 'POST',
                  headers: {
                    'Content-Type': 'application/json',
                    'X-FlyShelf-Client': 'MobileCompanion',
                    'X-Pairing-Key': pairingKeyRef.current || '',
                    'X-Source-Device': deviceName || 'Mobile',
                  },
                  body: JSON.stringify({ title: item.Title, content: item.Raw, type: item.Type, sourceDevice: deviceName })
                }, 5000).catch(() => {});
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
              const safeName = (item.Title || '').replace(/[^a-zA-Z0-9._-]/g, '_');
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
            } else {
              toast.error('File not cached locally', 'Please wait for the file to download or check your connection');
              return;
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
      if (!result.assets?.length) return;
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
      // Auto-send to PC via LAN/Cloudflare with fallback chain
      const pc = activeDevices.find((d: any) => d.DeviceType === 'PC');
      executeHeavyUpload(pc || 'Global', payload);
    } catch (err: any) { Alert.alert('Upload Failed', err?.message || 'Could not send file to PC. Check your connection in Settings.'); }
  };
  const launchDirectCamera = async () => {
    setIsCameraOptionsVisible(false);
    try {
      const result = await ImagePicker.launchCameraAsync({ mediaTypes: ['images'], allowsEditing: false, quality: 0.8 });
      if (!result.canceled) {
        if (!result.assets?.length) return;
        const file = result.assets[0];
        try { 
          const b64 = await FileSystem.readAsStringAsync(file.uri, { encoding: (FileSystem as any).EncodingType.Base64 }); 
          await Clipboard.setImageAsync(b64); 
          toast.clipboard("Photo Copied to Clipboard", "Ready to paste or send");
        } catch (e) { console.warn('Camera capture clipboard copy: error', (e as any)?.message || e); }
        const payload = { uri: file.uri, name: file.fileName || `camera_${NetworkClock.now()}.jpg`, size: file.fileSize, type: 'Image' };
        const pc = activeDevices.find((d: any) => d.DeviceType === 'PC');
        setPendingUploadPayload(payload);
        executeHeavyUpload(pc || 'Global', payload);
      }
    } catch (camErr: any) {
      Alert.alert('Camera Error', camErr?.message || 'Failed to launch camera');
    }
  };
  const pickImageAndSend = async () => {
    try {
      const result = await ImagePicker.launchImageLibraryAsync({ mediaTypes: ['images', 'videos'], allowsEditing: false, quality: 0.8 });
      if (!result.canceled) {
        if (!result.assets?.length) return;
        const file = result.assets[0];
        try { if (file.type === 'image') { const b64 = await FileSystem.readAsStringAsync(file.uri, { encoding: (FileSystem as any).EncodingType.Base64 }); await Clipboard.setImageAsync(b64); } } catch (e) { console.warn('Image picker clipboard copy: error', (e as any)?.message || e); }
        const payload = { uri: file.uri, name: file.fileName || `media_${NetworkClock.now()}`, size: file.fileSize, type: file.type === 'video' ? 'Video' : 'Image' };
        const pc = activeDevices.find((d: any) => d.DeviceType === 'PC');
        setPendingUploadPayload(payload);
        executeHeavyUpload(pc || 'Global', payload);
      }
    } catch (pickErr: any) {
      Alert.alert('Image Picker Error', pickErr?.message || 'Failed to open image library');
    }
  };
  const launchQRScanner = async () => { 
    setIsConnectModalVisible(false); 
    setIsCameraOptionsVisible(false); 
    if (!cameraPermission?.granted) { 
      const perm = await requestCameraPermission(); 
      if (!perm.granted) { 
        Alert.alert(
          'Camera Permission Required', 
          'Camera access is needed to scan FlyShelf pairing QR codes. Please allow camera access in Settings.',
          [
            { text: 'Open Settings', onPress: () => Linking.openSettings() },
            { text: 'Cancel', style: 'cancel' }
          ]
        ); 
        return; 
      } 
    } 
    setIsQRScannerActive(true); 
  };


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

            {/* ═══ PIN — local-only ═══ */}
            <TouchableOpacity onPress={async () => {
              try {
                // ZERO-TRUST: Toggle pin in local state only (no Firebase clipboard write)
                setClips(prev => prev.map(c => ((item.id && c.id === item.id) || (c.Title === item.Title && c.Raw === item.Raw)) ? {...c, IsPinned: !c.IsPinned} : c));
                toast.success(item.IsPinned ? "Unpinned" : "Pinned to Top", item.Title || "Clipboard Item");
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
                setLocalDeletedIds(prev => { const n = new Set(prev); n.add(item.id!); return n; });
                // Persist outside state updater to avoid side-effects in concurrent rendering
                AsyncStorage.getItem('localDeletedIds').then(raw => {
                  const ids: string[] = raw ? JSON.parse(raw) : [];
                  if (!ids.includes(item.id!)) {
                    ids.push(item.id!);
                    AsyncStorage.setItem('localDeletedIds', JSON.stringify(ids)).catch(() => {});
                  }
                }).catch(() => {});
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
  const scrollHandler = (e: any) => {
    const offsetY = e?.nativeEvent?.contentOffset?.y;
    if (typeof offsetY === 'number') {
      scrollY.value = offsetY;
    }
  };

  // ── Connection status for search bar ──
  const connectionStatus = useMemo((): 'online' | 'cloud' | 'reconnecting' | 'offline' => {
    // Priority 1: If the actual HTTP poll has recently succeeded, trust that
    if (connectionInfo) {
      return connectionInfo.type === 'LAN' ? 'online' : 'cloud';
    }
    // Priority 2: Firebase says PC is online but HTTP poll hasn't confirmed it yet
    const onlinePcs = pairedDevices.filter(d => d.deviceType === 'PC' && d.isOnline);
    if (onlinePcs.length > 0) {
      return 'reconnecting'; // Firebase sees PC but we haven't reached it directly yet
    }
    return 'offline';
  }, [pairedDevices, connectionInfo]);

  // ── Unified Smart Recent Activity Feed (Clips, Notes, Tasks, Vault) ──
  const recentActivityItems = useMemo((): ActivityItem[] => {
    const combined: ActivityItem[] = [];

    // 1. Recent Clipboard Items
    clips.slice(0, 15).forEach((clip, i) => {
      let icon: string = 'clipboard-outline';
      let iconColor: string = colors.accent.primary;
      const t = clip.Type || 'Text';

      if (t === 'Image' || t === 'ImageLink') { icon = 'image-outline'; iconColor = colors.type.image; }
      else if (t === 'Pdf' || t === 'Document') { icon = 'document-outline'; iconColor = colors.type.pdf; }
      else if (t === 'URL') { icon = 'link-outline'; iconColor = colors.type.url; }
      else if (t === 'Code') { icon = 'code-slash-outline'; iconColor = colors.type.code; }
      else if (t === 'File' || t === 'Archive') { icon = 'folder-outline'; iconColor = colors.accent.warning; }

      combined.push({
        id: `clip-${clip.id || i}`,
        type: 'clipboard',
        title: clip.Title || (clip.Raw || '').substring(0, 60) || 'Clipboard item',
        subtitle: `Clip • ${t}`,
        timestamp: clip.Timestamp || Date.now(),
        icon,
        iconColor,
        rawPayload: clip,
      });
    });

    // 2. Recent Notes (Bullets and sections)
    if (Array.isArray(recentNotes)) {
      recentNotes.forEach(day => {
        const dayTime = day.LastModified || (day.Date ? new Date(day.Date).getTime() : 0);
        if (Array.isArray(day.Bullets) && day.Bullets.length > 0) {
          day.Bullets.forEach((bullet: any) => {
            if (!bullet.Text?.trim()) return;
            const bulletTime = bullet.LastEdited ? new Date(bullet.LastEdited).getTime() : (bullet.Timestamp || dayTime);
            combined.push({
              id: `note-${bullet.Id || Math.random()}`,
              type: 'note',
              title: bullet.Text.trim().replace(/^[-*•]\s*/, ''),
              subtitle: `Note • ${day.Date || 'Daily note'}`,
              timestamp: bulletTime || Date.now(),
              icon: 'create-outline',
              iconColor: '#F59E0B',
              rawPayload: { date: day.Date, bulletId: bullet.Id },
            });
          });
        }
      });
    }

    // 3. Recent Tasks / Todos
    if (Array.isArray(recentTodos)) {
      recentTodos.forEach(day => {
        if (Array.isArray(day.Items)) {
          day.Items.forEach((task: any) => {
            if (!task.Text?.trim()) return;
            const taskTime = task.CompletedTimestamp || task.CreatedTimestamp || (day.Date ? new Date(day.Date).getTime() : 0);
            combined.push({
              id: `todo-${task.Id || Math.random()}`,
              type: 'task',
              title: task.Text.trim(),
              subtitle: task.Completed ? 'Task completed' : 'Active task',
              timestamp: taskTime || Date.now(),
              icon: task.Completed ? 'checkmark-circle-outline' : 'checkbox-outline',
              iconColor: task.Completed ? '#10B981' : '#6366F1',
              rawPayload: { taskId: task.Id, date: day.Date },
            });
          });
        }
      });
    }

    // 4. Recent Vault / Storage Entries
    if (Array.isArray(manifest?.entries)) {
      manifest.entries.forEach(entry => {
        combined.push({
          id: `vault-${entry.id}`,
          type: 'vault',
          title: entry.originalName || 'Storage file',
          subtitle: `Storage • ${entry.mimeType?.split('/')[1] || 'File'}`,
          timestamp: entry.dateAdded || Date.now(),
          icon: 'cube-outline',
          iconColor: '#8B5CF6',
          rawPayload: entry,
        });
      });
    }

    // Chronologically sort all interactions: most recent first
    combined.sort((a, b) => (b.timestamp || 0) - (a.timestamp || 0));
    return combined.slice(0, 30);
  }, [clips, recentNotes, recentTodos, manifest, colors]);

  // ── Smart Navigation Handler for Recent Activity ──
  const handleActivityItemPress = useCallback((item: ActivityItem) => {
    try { Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); } catch {}
    if (item.type === 'clipboard') {
      setViewMode('clipboard');
      if (item.rawPayload?.Raw) {
        Clipboard.setStringAsync(item.rawPayload.Raw).catch(() => {});
        toast.clipboard('Copied from Activity', item.rawPayload.Raw);
      }
    } else if (item.type === 'note') {
      router.push('/(tabs)/notes');
    } else if (item.type === 'task') {
      router.push('/(tabs)/todo');
    } else if (item.type === 'vault') {
      router.push('/(tabs)/vault' as any);
    }
  }, []);

  // ── Render Home Dashboard ──
  const renderHomeDashboard = () => (
    <View style={{ flex: 1 }}>
      <ScrollView
        style={homeStyles.container}
        contentContainerStyle={[homeStyles.scrollContent, { paddingTop: insets.top + 12 }]}
        showsVerticalScrollIndicator={false}
        onScroll={scrollHandler}
        scrollEventThrottle={16}
      >
        {/* Greeting Header */}
        <View style={{ paddingHorizontal: 20, marginBottom: 6 }}>
          <Text style={{ fontSize: 26, fontFamily: font.bold, color: colors.text.primary }}>
            {new Date().getHours() < 12 ? 'Good Morning' : new Date().getHours() < 17 ? 'Good Afternoon' : 'Good Evening'}
          </Text>
          <Text style={{ fontSize: 13, fontFamily: font.medium, color: colors.text.tertiary, marginTop: 2 }}>
            {deviceName || 'FlyShelf'}
            {connectionStatus === 'online' ? ' • 🟢 Connected' : connectionStatus === 'cloud' ? ' • 🟡 Cloud' : connectionStatus === 'reconnecting' ? ' • 🟡 Reconnecting...' : ' • Offline'}
          </Text>
        </View>

        {/* Category Tiles FIRST — 2×3 Compact Grid */}
        <View style={homeStyles.tilesGrid}>
          <CategoryTile
            icon="clipboard-outline"
            label="Clipboard"
            subtitle={`${filteredClips.length} item${filteredClips.length !== 1 ? 's' : ''}`}
            iconColor={colors.accent.primary}
            onPress={() => setViewMode('clipboard')}
          />
          <CategoryTile
            icon="folder-outline"
            label="Files"
            subtitle="Browse files"
            iconColor={colors.type.image}
            onPress={() => router.push('/(tabs)/archive')}
          />
          <CategoryTile
            icon="cube-outline"
            label="Storage"
            subtitle="Offline files"
            iconColor={colors.accent.info}
            onPress={() => router.push('/(tabs)/vault' as any)}
          />
          <CategoryTile
            icon="document-text-outline"
            label="Notes"
            subtitle="Daily notes"
            iconColor={colors.accent.success}
            onPress={() => router.push('/(tabs)/notes')}
          />
          <CategoryTile
            icon="checkbox-outline"
            label="Tasks"
            subtitle="Manage tasks"
            iconColor={colors.accent.warning}
            onPress={() => router.push('/(tabs)/todo')}
          />
          <CategoryTile
            icon="settings-outline"
            label="Settings"
            subtitle="Preferences"
            iconColor={colors.text.secondary}
            onPress={() => router.push('/(tabs)/settings')}
          />
        </View>

        {/* Material Search Bar — BELOW tiles */}
        <View style={{ paddingHorizontal: 20, marginTop: 16 }}>
          <MaterialSearchBar
            connectionStatus={connectionStatus}
            onSearch={(q) => { setFeedSearch(q); setViewMode('clipboard'); }}
            onSettingsPress={() => router.push('/settings-modal' as any)}
            onConnectionPress={() => setShowNetworkDashboard(true)}
          />
        </View>

        {/* Quick Action Chips */}
        <QuickActionChips
          onScanDocument={() => router.push('/pdf-tools')}
          onSendFile={pickFileAndSend}
          onPdfTools={() => router.push('/pdf-tools')}
        />

        {/* Recent Activity Feed */}
        <RecentActivityFeed
          items={recentActivityItems}
          onItemPress={handleActivityItemPress}
          onSeeAll={() => setViewMode('clipboard')}
        />
      </ScrollView>

      {/* FAB */}
      <HomeFab
        onSendText={() => setIsSendTextModalVisible(true)}
        onCamera={launchDirectCamera}
        onSendPhoto={pickImageAndSend}
        onSendFile={pickFileAndSend}
        onScanQr={launchQRScanner}
      />
    </View>
  );

  // RENDER
  // ════════════════════════════════════════════════════════

  return (
    <LinearGradient colors={[colors.bg.base, colors.bg.baseEnd]} style={{ flex: 1 }}>
    <View style={[styles.container, { backgroundColor: 'transparent' }]}>
      {/* Device Name Setup Modal — only show after settings are loaded from storage */}
      <Modal visible={!settingsLoading && !deviceName} animationType="fade" transparent={true}>
        <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : 'height'} style={styles.modalOverlay}>
          <View style={styles.modalContent}>
            <Text style={styles.modalTitle}>Name this Device</Text>
            <Text style={styles.modalSubtitle}>Identify this device in the FlyShelf network.</Text>
            <TextInput style={styles.modalInput} value={setupName} onChangeText={setSetupName} placeholder="e.g. Galaxy S23" placeholderTextColor="#4C5361" autoFocus accessibilityLabel="Device name" accessibilityRole="text" />
            <TouchableOpacity style={styles.modalButton} onPress={() => { if(setupName.trim()) setDeviceName(setupName.trim()); }} accessibilityLabel="Get started" accessibilityRole="button"><Text style={styles.modalButtonText}>Get Started</Text></TouchableOpacity>
          </View>
        </KeyboardAvoidingView>
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
        <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : 'height'} style={styles.modalOverlay}>
          <ScrollView contentContainerStyle={{ flexGrow: 0 }} keyboardShouldPersistTaps="handled" bounces={false}>
            <View style={styles.modalContent}>
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
            </View>
          </ScrollView>
        </KeyboardAvoidingView>
      </Modal>

      {/* QR Scanner — Enhanced with High-Tech Viewfinder & Torch Control */}
      {isQRScannerActive && (
        <Modal visible={isQRScannerActive} animationType="fade" transparent={false} onRequestClose={() => setIsQRScannerActive(false)}>
          <View style={{flex: 1, backgroundColor: '#000'}}>
            <AppErrorBoundary fallbackTitle="Camera error">
              <CameraView
                style={StyleSheet.absoluteFill}
                facing="back"
                enableTorch={isTorchOn}
                barcodeScannerSettings={{ barcodeTypes: ["qr"] }}
                onBarcodeScanned={handleBarcodeScanned}
              />
            </AppErrorBoundary>

            {/* Viewfinder Reticle Overlay */}
            <View style={StyleSheet.absoluteFill} pointerEvents="none">
              <View style={{ flex: 1, backgroundColor: 'rgba(0,0,0,0.65)' }} />
              <View style={{ height: 260, flexDirection: 'row' }}>
                <View style={{ flex: 1, backgroundColor: 'rgba(0,0,0,0.65)' }} />
                <View style={{
                  width: 260,
                  height: 260,
                  borderRadius: 24,
                  borderWidth: 2,
                  borderColor: colors.accent.primary,
                  backgroundColor: 'transparent',
                  overflow: 'hidden',
                }}>
                  {/* Corner Reticle Marks */}
                  <View style={{ position: 'absolute', top: 10, left: 10, width: 22, height: 22, borderTopWidth: 4, borderLeftWidth: 4, borderColor: '#FFF' }} />
                  <View style={{ position: 'absolute', top: 10, right: 10, width: 22, height: 22, borderTopWidth: 4, borderRightWidth: 4, borderColor: '#FFF' }} />
                  <View style={{ position: 'absolute', bottom: 10, left: 10, width: 22, height: 22, borderBottomWidth: 4, borderLeftWidth: 4, borderColor: '#FFF' }} />
                  <View style={{ position: 'absolute', bottom: 10, right: 10, width: 22, height: 22, borderBottomWidth: 4, borderRightWidth: 4, borderColor: '#FFF' }} />
                </View>
                <View style={{ flex: 1, backgroundColor: 'rgba(0,0,0,0.65)' }} />
              </View>
              <View style={{ flex: 1, backgroundColor: 'rgba(0,0,0,0.65)', alignItems: 'center', paddingTop: 24 }}>
                <Text style={{ color: '#FFF', fontSize: 16, fontFamily: font.bold, textAlign: 'center' }}>
                  Scan PC Pairing QR Code
                </Text>
                <Text style={{ color: colors.text.secondary, fontSize: 13, fontFamily: font.medium, textAlign: 'center', marginTop: 6, paddingHorizontal: 40 }}>
                  Align the QR code shown on your PC screen within the frame
                </Text>
              </View>
            </View>

            {/* Top Controls: Close & Torch */}
            <View style={{ position: 'absolute', top: insets.top + 16, left: 20, right: 20, flexDirection: 'row', justifyContent: 'space-between', zIndex: 10 }}>
              <TouchableOpacity
                style={{ width: 44, height: 44, borderRadius: 22, backgroundColor: 'rgba(0,0,0,0.6)', alignItems: 'center', justifyContent: 'center', borderWidth: 1, borderColor: 'rgba(255,255,255,0.2)' }}
                onPress={() => setIsQRScannerActive(false)}
                accessibilityLabel="Close QR scanner"
                accessibilityRole="button"
              >
                <Ionicons name="close" size={24} color="#FFF" />
              </TouchableOpacity>

              <TouchableOpacity
                style={{ width: 44, height: 44, borderRadius: 22, backgroundColor: isTorchOn ? colors.accent.warning : 'rgba(0,0,0,0.6)', alignItems: 'center', justifyContent: 'center', borderWidth: 1, borderColor: 'rgba(255,255,255,0.2)' }}
                onPress={() => setIsTorchOn(!isTorchOn)}
                accessibilityLabel={isTorchOn ? 'Turn flashlight off' : 'Turn flashlight on'}
                accessibilityRole="button"
              >
                <Ionicons name={isTorchOn ? "flashlight" : "flashlight-outline"} size={22} color={isTorchOn ? '#000' : '#FFF'} />
              </TouchableOpacity>
            </View>

            {/* Bottom Cancel Pill */}
            <View style={{ position: 'absolute', bottom: insets.bottom + 32, left: 0, right: 0, alignItems: 'center', zIndex: 10 }}>
              <TouchableOpacity
                style={{ backgroundColor: 'rgba(255,255,255,0.18)', paddingHorizontal: 28, paddingVertical: 14, borderRadius: 28, borderWidth: 1, borderColor: 'rgba(255,255,255,0.3)' }}
                onPress={() => setIsQRScannerActive(false)}
                accessibilityLabel="Cancel scanner"
                accessibilityRole="button"
              >
                <Text style={{ color: '#FFF', fontFamily: font.bold, fontSize: 15 }}>Cancel Scan</Text>
              </TouchableOpacity>
            </View>
          </View>
        </Modal>
      )}

      {/* Send Text to PC Modal */}
      <SendTextModal
        visible={isSendTextModalVisible}
        onClose={() => setIsSendTextModalVisible(false)}
        onSend={transmitTextSecurely}
        targetDeviceName={String(activeDevices.find((d: any) => d.DeviceType === 'PC')?.DeviceName || 'PC')}
        isSending={isSending}
      />

      {/* Main Content: Home Dashboard vs Clipboard Feed */}
      {viewMode === 'home' ? (
        renderHomeDashboard()
      ) : (
        <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : undefined} style={{flex: 1}}>
        {/* Clipboard Sub-Screen Header */}
        <View style={{ flexDirection: 'row', alignItems: 'center', paddingHorizontal: 16, paddingTop: insets.top + 8, paddingBottom: 12 }}>
          <TouchableOpacity
            onPress={() => setViewMode('home')}
            style={{ padding: 8, marginRight: 8, backgroundColor: colors.bg.cardHover, borderRadius: 10 }}
            accessibilityLabel="Back to Home"
            accessibilityRole="button"
          >
            <Ionicons name="arrow-back" size={22} color={colors.text.primary} />
          </TouchableOpacity>
          <View style={{ flex: 1 }}>
            <Text style={{ fontSize: 20, fontFamily: font.bold, color: colors.text.primary }}>Clipboard</Text>
            <View style={{ flexDirection: 'row', alignItems: 'center', gap: 4, marginTop: 2 }}>
              <View style={{
                width: 7, height: 7, borderRadius: 4,
                backgroundColor: connectionStatus === 'online' ? '#34D399' : connectionStatus === 'cloud' ? '#FBBF24' : connectionStatus === 'reconnecting' ? '#FBBF24' : '#F87171'
              }} />
              <Text style={{ fontSize: 11, fontFamily: font.medium, color: colors.text.tertiary }}>
                {filteredClips.length} item{filteredClips.length !== 1 ? 's' : ''} • {connectionStatus === 'online' ? 'LAN' : connectionStatus === 'cloud' ? 'Cloud' : connectionStatus === 'reconnecting' ? 'Reconnecting...' : 'Offline'}
              </Text>
            </View>
          </View>
          <TouchableOpacity onPress={() => setGlobalSyncEnabled(!isGlobalSyncEnabled)} style={{ padding: 10, backgroundColor: isGlobalSyncEnabled ? colors.accent.successDim : colors.bg.cardHover, borderRadius: 10, marginRight: 8 }} accessibilityLabel={isGlobalSyncEnabled ? 'Disable cloud sync' : 'Enable cloud sync'} accessibilityRole="button">
            <Ionicons name={isGlobalSyncEnabled ? 'cloud' : 'cloud-outline'} size={20} color={isGlobalSyncEnabled ? colors.accent.success : colors.text.tertiary} />
          </TouchableOpacity>
          <TouchableOpacity onPress={clearAllClips} style={{ padding: 10, backgroundColor: colors.bg.cardHover, borderRadius: 10 }} accessibilityLabel="Clear all clips" accessibilityRole="button">
            <Ionicons name="trash-outline" size={20} color={colors.accent.error} />
          </TouchableOpacity>
        </View>

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
          {renderConnectionBanner()}
          {!isStorageLoaded && clips.length === 0 && <SkeletonLoader />}
          {filteredClips.length === 0 && isStorageLoaded ? (
            <View style={{ flex: 1, alignItems: 'center', justifyContent: 'center', paddingHorizontal: 32, marginTop: 60 }}>
              <Text style={{ fontSize: 48, marginBottom: 16 }}>📋</Text>
              <Text style={{ fontSize: 18, fontFamily: font.bold, color: colors.text.primary, marginBottom: 8 }}>No clips synced yet</Text>
              <Text style={{ fontSize: 14, fontFamily: font.medium, color: colors.text.secondary, textAlign: 'center', marginBottom: 16 }}>
                Copy something on your PC to see it appear here
              </Text>
              {pairedDevices.length === 0 && (
                <Text style={{ fontSize: 13, fontFamily: font.medium, color: colors.accent.primary, textAlign: 'center' }}>
                  Tap the connect button above to get started
                </Text>
              )}
            </View>
          ) : filteredClips.length === 0 ? null : (
            <FlatList
              ref={feedListRef}
              refreshControl={<RefreshControl refreshing={isRefreshing} onRefresh={onRefresh} tintColor="#6384FF" colors={['#6384FF']} />}
              data={filteredClips}
              keyExtractor={(item: any, index: number) => item.id ? item.id : index.toString()}
              showsVerticalScrollIndicator={false}
              contentContainerStyle={{ paddingBottom: 110 }}
              initialNumToRender={12}
              maxToRenderPerBatch={12}
              windowSize={9}
              removeClippedSubviews={Platform.OS === 'android'}
              keyboardShouldPersistTaps="handled"
              keyboardDismissMode="on-drag"
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
                if (!selected.length) return; 
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
                  await Share.share({ message: text }).catch(() => { 
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

        </KeyboardAvoidingView>
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

      {/* Lower Home Switcher Floating Button — REMOVED: overlapped bottom tab bar */}

      <NetworkDashboard
        visible={showNetworkDashboard}
        onClose={() => setShowNetworkDashboard(false)}
        pcUrl={cachedPcUrlRef.current}
        pairingKey={pairingKeyRef.current || null}
        onPairPress={() => { setShowNetworkDashboard(false); setIsConnectModalVisible(true); }}
      />
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
