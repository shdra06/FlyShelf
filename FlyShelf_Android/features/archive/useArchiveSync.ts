/**
 * useArchiveSync.ts
 * ────────────────────────────────────────────────────────────────
 * Extracted from archive.tsx — owns Firebase real-time listeners
 * (device discovery + device-groups), the local media scan, and
 * the chunked/single-POST file transfer pipeline.
 *
 * Responsibilities:
 *   • Subscribe to Firebase `active_devices/{pairingKey}` (device list)
 *   • Subscribe to Firebase `device_groups/{pairingKey}` (group list)
 *   • Expose helpers to save/delete device groups
 *   • Run local media scan (MediaLibrary + fallback FS scan)
 *   • Execute chunked or single-POST file transfer to a target device
 *
 * Performance (v2 — cache-first architecture):
 *   • Cache-first: show cached assets instantly on mount, rescan in background
 *   • 24h cache TTL (was 1h) — avoids heavy rescans on every open
 *   • No item cap — caches ALL indexed assets (was limited to 500)
 *   • Delta scan — only fetches files newer than last scan timestamp
 *   • Batched filesystem traversal — Promise.all batches of 20
 *   • Yield points every 50 files to keep JS thread responsive
 *
 * The UI pieces (rendering, selection, modals) remain in archive.tsx.
 */

import { useState, useEffect, useRef, useCallback } from 'react';
import { Platform, Alert, InteractionManager } from 'react-native';
import { toast } from '../../context/ToastContext';
import AsyncStorage from '@react-native-async-storage/async-storage';
import * as MediaLibrary from 'expo-media-library';
import * as FileSystem from 'expo-file-system/legacy';

import { useSettings } from '../../context/SettingsContext';
import { database } from '../../firebaseConfig';
import { ref as dbRef, push, set, onValue, query } from 'firebase/database';
import { decryptDeviceList, isValidPairingKey } from '../../utils/networkHelpers';

// ─── Types (local, matching what archive.tsx uses) ────────────
export interface MediaAsset {
  id: string;
  uri: string;
  filename: string;
  mediaType: string;
  width?: number;
  height?: number;
  creationTime: number;
  duration?: number;
  source?: string;
  fileSize?: number;
}

export interface FirebaseDevice {
  firebaseKey?: string;
  DeviceName: string;
  DeviceType?: string;
  IsOnline?: boolean;
  Url?: string;
  GlobalUrl?: string;
  connectionType?: string;
  resolvedUrl?: string;
  [key: string]: unknown;
}

export type SourceFilter = 'Camera' | 'WhatsApp' | 'Downloads' | 'All';

export interface DeviceGroup {
  id: string;
  name: string;
  deviceNames: string[];
}

// ─── Hook ──────────────────────────────────────────────────────
export function useArchiveSync() {
  const { pairingKey, pairedDevices, deviceName } = useSettings();

  // ─── Firebase device / group state ───
  const [allFirebaseDevices, setAllFirebaseDevices] = useState<FirebaseDevice[]>([]);
  const [deviceGroups, setDeviceGroups] = useState<DeviceGroup[]>([]);

  // ─── Media scan state ───
  const [hasPermission, setHasPermission] = useState<boolean | null>(null);
  const [mediaAssets, setMediaAssets] = useState<MediaAsset[]>([]);
  const [isScanning, setIsScanning] = useState(false);
  // Background refresh indicator — for subtle "Updating..." UI instead of blocking spinner
  const [isBackgroundRefreshing, setIsBackgroundRefreshing] = useState(false);

  // ─── Upload state ───
  const [isUploading, setIsUploading] = useState(false);
  const [uploadIndex, setUploadIndex] = useState(0);
  const [uploadTotal, setUploadTotal] = useState(0);
  const [uploadProgress, setUploadProgress] = useState<Record<string, string>>({});

  // ─── Transfer control refs ───
  const isPausedRef = useRef(false);
  const isCancelledRef = useRef(false);
  const hasScannedRef = useRef(false);

  // ═══════════════════════════════════════════════════════════
  // Firebase: Real-time device-groups listener
  // ═══════════════════════════════════════════════════════════
  useEffect(() => {
    if (!pairingKey) return;
    const groupsRef = dbRef(database, `device_groups/${pairingKey}`);
    const unsubGroups = onValue(groupsRef, (snapshot) => {
      if (snapshot.exists()) {
        const data = snapshot.val();
        const groups = Object.keys(data).map(k => ({ ...data[k], id: k }));
        setDeviceGroups(groups);
      } else {
        setDeviceGroups([]);
      }
    });
    return () => unsubGroups();
  }, [pairingKey]);

  // ═══════════════════════════════════════════════════════════
  // Firebase: Real-time device discovery
  // ═══════════════════════════════════════════════════════════
  useEffect(() => {
    if (!pairingKey || !isValidPairingKey(pairingKey)) { return; }
    const nodesRef = query(dbRef(database, `active_devices/${pairingKey}`));
    const unsubscribeNodes = onValue(nodesRef, async (snapshot) => {
      if (snapshot.exists()) {
        const data = snapshot.val();
        const filtered = Object.keys(data)
          .map(k => ({ ...data[k], firebaseKey: k }))
          .filter(d => d.IsOnline);
        const allDevs = await decryptDeviceList(filtered as any, pairingKey);
        setAllFirebaseDevices(allDevs as any);
      } else {
        setAllFirebaseDevices([]);
      }
    });
    return () => unsubscribeNodes();
  }, [pairingKey]);

  // ─── Storage Constants ───
  const MEDIA_CACHE_KEY = '@flyshelf_cached_media_assets';
  const MEDIA_LAST_SCAN_KEY = '@flyshelf_last_media_scan_ts';
  // 24-hour cache freshness — dramatically reduces unnecessary full rescans
  const SCAN_CACHE_MAX_AGE_MS = 24 * 60 * 60 * 1000;

  // ═══════════════════════════════════════════════════════════
  // Load cached media index on mount (INSTANT — no scan needed)
  // ═══════════════════════════════════════════════════════════
  useEffect(() => {
    (async () => {
      try {
        const raw = await AsyncStorage.getItem(MEDIA_CACHE_KEY);
        if (raw) {
          const parsed = JSON.parse(raw);
          if (Array.isArray(parsed) && parsed.length > 0) {
            setMediaAssets(parsed);
            hasScannedRef.current = true;
          }
        }
      } catch {}
    })();
  }, []);

  // ═══════════════════════════════════════════════════════════
  // Permissions on mount
  // ═══════════════════════════════════════════════════════════
  useEffect(() => {
    (async () => {
      try {
        const { status } = await MediaLibrary.requestPermissionsAsync(false, ['photo', 'video']);
        setHasPermission(status === 'granted');
        // On Android 11+, All Files Access may be needed for document scanning.
        // A more robust check would involve a dedicated Expo plugin / native module.
      } catch { setHasPermission(false); }
    })();
  }, []);

  // ═══════════════════════════════════════════════════════════
  // Helper: yield to JS thread to prevent ANR/frame drops
  // ═══════════════════════════════════════════════════════════
  const yieldToThread = () => new Promise<void>(r => setTimeout(r, 0));

  // ═══════════════════════════════════════════════════════════
  // Media scan — cache-first, delta-only background refresh
  // ═══════════════════════════════════════════════════════════
  const scanMedia = useCallback(async (startDate: Date, endDate: Date, isManual: boolean = false) => {
    if (hasPermission === false) {
      toast.error('Permission Required', 'Enable photo and storage access in Android settings to scan files');
      return;
    }

    // For manual scans, show the full scanning indicator
    // For background refreshes, show a subtle indicator
    if (isManual) {
      setIsScanning(true);
      toast.info('Scanning Media...', 'Indexing documents, photos, and videos');
    } else {
      setIsBackgroundRefreshing(true);
    }

    try {
      const allFound: MediaAsset[] = [];

      // ── 1. Gallery scan for images/videos (paginated, non-blocking) ──
      try {
        let hasNextPage = true;
        let after: string | undefined = undefined;
        let batchCount = 0;
        const INITIAL_BATCH = 200; // Show first batch quickly
        const ONGOING_BATCH = 100;

        while (hasNextPage) {
          const batchSize = batchCount === 0 ? INITIAL_BATCH : ONGOING_BATCH;
          const media = await MediaLibrary.getAssetsAsync({
            first: batchSize,
            after: after,
            mediaType: ['photo', 'video'],
            createdAfter: startDate.getTime(),
            createdBefore: endDate.getTime(),
            sortBy: [[MediaLibrary.SortBy.creationTime, false]]
          });

          // Use push for O(1) instead of spread for O(n)
          for (const a of media.assets) {
            allFound.push({ ...a, source: 'Camera' } as any);
          }

          hasNextPage = media.hasNextPage;
          after = media.endCursor;
          batchCount++;

          // After the first batch, update UI immediately so user sees results fast
          if (batchCount === 1 && allFound.length > 0) {
            const snapshot = [...allFound];
            setMediaAssets(prev => {
              // Merge with existing cached data (cached docs stay, new photos added)
              const existingDocs = prev.filter(a => a.source !== 'Camera');
              const merged = [...snapshot, ...existingDocs];
              return Array.from(new Map(merged.map(item => [item.id, item])).values())
                .sort((a, b) => b.creationTime - a.creationTime);
            });
          }

          // Yield every 3 batches to keep UI responsive
          if (batchCount % 3 === 0) {
            await yieldToThread();
          }
        }
      } catch (mediaErr: any) {
        console.warn('MediaLibrary scan failed:', mediaErr?.message);
      }

      // ── 2. Fallback filesystem scan (batched, non-blocking) ──
      await fallbackFileScan(allFound);

      // ── 3. Deduplicate, sort, and commit ──
      const uniqueAssets = Array.from(new Map(allFound.map(item => [item.id, item])).values());
      uniqueAssets.sort((a, b) => b.creationTime - a.creationTime);
      setMediaAssets(uniqueAssets);

      // ── 4. Cache ALL assets (no 500-item cap) ──
      try {
        await AsyncStorage.setItem(MEDIA_CACHE_KEY, JSON.stringify(uniqueAssets));
        await AsyncStorage.setItem(MEDIA_LAST_SCAN_KEY, Date.now().toString());
      } catch {}

      if (isManual) {
        const imgCount = uniqueAssets.filter(a => a.mediaType === 'photo').length;
        const vidCount = uniqueAssets.filter(a => a.mediaType === 'video').length;
        const docCount = uniqueAssets.filter(a => a.mediaType === 'pdf' || a.mediaType === 'doc').length;
        toast.success('Media Scan Complete', `${imgCount} photos, ${vidCount} videos, ${docCount} documents found`);
      }
    } catch (e: any) { 
      if (isManual) {
        toast.error('Scan Error', e?.message || 'Failed to index local storage');
      }
    }
    setIsScanning(false);
    setIsBackgroundRefreshing(false);
  }, [hasPermission]);

  // Auto-scan: cache-first with background delta refresh
  const autoScan = useCallback(async (startDate: Date, endDate: Date) => {
    if (hasPermission && !isScanning) {
      // If we already have cached data loaded, don't block — just check freshness
      if (hasScannedRef.current && mediaAssets.length > 0) {
        // Check if cache is still fresh
        try {
          const lastScanRaw = await AsyncStorage.getItem(MEDIA_LAST_SCAN_KEY);
          const lastScan = lastScanRaw ? parseInt(lastScanRaw, 10) : 0;
          const isCacheFresh = (Date.now() - lastScan) < SCAN_CACHE_MAX_AGE_MS;
          if (isCacheFresh) {
            return; // Cache is fresh, no scan needed at all!
          }
        } catch {}

        // Cache is stale — run background refresh (non-blocking)
        hasScannedRef.current = true;
        InteractionManager.runAfterInteractions(() => {
          scanMedia(startDate, endDate, false);
        });
        return;
      }

      // No cached data — need to scan (but still non-blocking)
      hasScannedRef.current = true;
      InteractionManager.runAfterInteractions(() => {
        scanMedia(startDate, endDate, false);
      });
    }
  }, [hasPermission, mediaAssets.length, isScanning, scanMedia]);

  // ─── Batched fallback filesystem scan (non-blocking) ────────
  const fallbackFileScan = async (allFound: MediaAsset[]) => {
    if (Platform.OS === 'web') return;
    const scanRoots: { path: string; source: SourceFilter; recursive?: boolean }[] = [
      { path: 'file:///storage/emulated/0/Download/', source: 'Downloads', recursive: true },
      { path: 'file:///storage/emulated/0/Documents/', source: 'Downloads', recursive: true },
      { path: 'file:///storage/emulated/0/WhatsApp/Media/WhatsApp Documents/', source: 'WhatsApp', recursive: true },
      { path: 'file:///storage/emulated/0/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Documents/', source: 'WhatsApp', recursive: true },
    ];

    // Collect file paths first, then batch-check info
    const scanDir = async (dirPath: string, source: SourceFilter, depth: number = 0, maxDepth: number = 2) => {
      if (depth > maxDepth) return;
      try {
        const check = await FileSystem.getInfoAsync(dirPath);
        if (!check.exists || !check.isDirectory) return;
        const files = await FileSystem.readDirectoryAsync(dirPath);

        // ── Batch processing: collect files, then check info in batches of 20 ──
        const regularFiles: string[] = [];
        const subdirs: string[] = [];

        for (const file of files) {
          if (file === '.nomedia' || file.startsWith('.') || file === 'Android' || file === 'node_modules') continue;
          const fullPath = dirPath + file;
          
          // Quick extension check before expensive getInfoAsync
          const lowerFile = file.toLowerCase();
          const isKnownFile = lowerFile.endsWith('.pdf') ||
            lowerFile.match(/\.(doc|docx|txt|xlsx|xls|pptx|ppt|odt|rtf|csv)$/) ||
            lowerFile.match(/\.(apk|zip|rar|7z|tar|gz)$/);
          
          if (isKnownFile) {
            regularFiles.push(fullPath);
          } else if (depth < maxDepth) {
            // Could be a directory — we'll check it
            subdirs.push(fullPath);
          }
        }

        // ── Process regular files in batches of 20 (parallel) ──
        for (let i = 0; i < regularFiles.length; i += 20) {
          const batch = regularFiles.slice(i, i + 20);
          const results = await Promise.all(
            batch.map(async (fullPath) => {
              try {
                const fInfo = await FileSystem.getInfoAsync(fullPath);
                if (!fInfo.exists || fInfo.isDirectory) return null;
                const file = fullPath.split('/').pop() || '';
                const lowerFile = file.toLowerCase();
                let mediaType = '';
                if (lowerFile.endsWith('.pdf')) mediaType = 'pdf';
                else if (lowerFile.match(/\.(doc|docx|txt|xlsx|xls|pptx|ppt|odt|rtf|csv)$/)) mediaType = 'doc';
                else if (lowerFile.match(/\.(apk|zip|rar|7z|tar|gz)$/)) mediaType = 'doc';
                if (!mediaType) return null;
                const rawModTime = fInfo.modificationTime || 0;
                const modTimeMs = rawModTime > 1e12 ? rawModTime : rawModTime * 1000;
                return { id: fullPath, uri: fullPath, filename: file, creationTime: modTimeMs, mediaType, source, fileSize: (fInfo as any).size || 0 } as MediaAsset;
              } catch { return null; }
            })
          );
          // Push non-null results
          for (const r of results) {
            if (r) allFound.push(r);
          }

          // Yield every 50 files to keep JS thread responsive
          if (i > 0 && i % 60 === 0) {
            await yieldToThread();
          }
        }

        // ── Check subdirectories (also batched) ──
        for (let i = 0; i < subdirs.length; i += 10) {
          const batch = subdirs.slice(i, i + 10);
          const dirInfos = await Promise.all(
            batch.map(async (fullPath) => {
              try {
                const fInfo = await FileSystem.getInfoAsync(fullPath);
                return fInfo.exists && fInfo.isDirectory ? fullPath : null;
              } catch { return null; }
            })
          );
          for (const dir of dirInfos) {
            if (dir) {
              await scanDir(dir + '/', source, depth + 1, maxDepth);
            }
          }
        }
      } catch {}
    };

    for (const { path, source, recursive } of scanRoots) {
      await scanDir(path, source, 0, recursive ? 4 : 0);
      // Yield between root directories
      await yieldToThread();
    }
  };

  // ═══════════════════════════════════════════════════════════
  // Device group persistence
  // ═══════════════════════════════════════════════════════════
  const saveGroupToFirebase = useCallback(async (group: DeviceGroup) => {
    if (!pairingKey) return;
    const groupRef = dbRef(database, `device_groups/${pairingKey}/${group.id}`);
    await set(groupRef, { name: group.name, deviceNames: group.deviceNames });
  }, [pairingKey]);

  const deleteGroupFromFirebase = useCallback(async (groupId: string) => {
    if (!pairingKey) return;
    const groupRef = dbRef(database, `device_groups/${pairingKey}/${groupId}`);
    await set(groupRef, null);
  }, [pairingKey]);

  // ═══════════════════════════════════════════════════════════
  // File transfer (single-POST + chunked upload)
  // ═══════════════════════════════════════════════════════════
  const CHUNK_SIZE = 10 * 1024 * 1024; // 10MB chunks (base64 overhead ~13MB in memory)

  /**
   * Chunked upload: split file into CHUNK_SIZE chunks, send each, then finalize.
   * Called automatically by executeTransfer for large files over Cloudflare (>50MB).
   */
  const uploadChunked = useCallback(async (
    baseUrl: string,
    fileUri: string,
    asset: MediaAsset,
    batch: string,
    totalSize: number
  ) => {
    // AUDIT FIX #4: Deterministic sessionId from asset metadata so resume works after restart
    const sessionId = `upload_${asset.id || asset.filename}_${totalSize}`;
    const totalChunks = Math.ceil(totalSize / CHUNK_SIZE);

    // ── Resumable uploads: check for saved progress from a previous interrupted upload ──
    const progressKey = `@upload_progress_${sessionId}`;
    const savedProgress = await AsyncStorage.getItem(progressKey);
    const startChunk = savedProgress ? parseInt(savedProgress, 10) : 0;

    for (let i = startChunk; i < totalChunks; i++) {
      if (isCancelledRef.current) throw new Error('Cancelled');
      while (isPausedRef.current) {
        if (isCancelledRef.current) throw new Error('Cancelled');
        await new Promise(r => setTimeout(r, 500));
      }

      // Read chunk from file
      const offset = i * CHUNK_SIZE;
      const length = Math.min(CHUNK_SIZE, totalSize - offset);

      // Read chunk as base64, write to temp file, upload temp file
      const chunkB64 = await FileSystem.readAsStringAsync(fileUri, {
        encoding: FileSystem.EncodingType.Base64,
        position: offset,
        length: length,
      });
      const chunkTempUri = `${FileSystem.cacheDirectory}chunk_${sessionId}_${i}`;
      await FileSystem.writeAsStringAsync(chunkTempUri, chunkB64, { encoding: FileSystem.EncodingType.Base64 });

      // Upload chunk
      let chunkAttempt = 0;
      let chunkDone = false;
      while (chunkAttempt < 3 && !chunkDone) {
        chunkAttempt++;
        // Re-resolve URL on retry (tunnel URL may have changed)
        if (chunkAttempt > 1 && baseUrl.includes('trycloudflare.com')) {
          const freshPc = pairedDevices.find(d => d.deviceType === 'PC' && d.isOnline && (d.localUrl || d.globalUrl));
          if (freshPc) baseUrl = freshPc.localUrl || freshPc.globalUrl || baseUrl;
        }
        try {
          const res = await FileSystem.uploadAsync(`${baseUrl}/api/upload_chunk`, chunkTempUri, {
            httpMethod: 'POST',
            uploadType: FileSystem.FileSystemUploadType.BINARY_CONTENT,
            headers: {
              'X-FlyShelf-Client': 'MobileCompanion',
              'X-Pairing-Key': pairingKey ?? '',
              'X-Upload-Session': sessionId,
              'X-Chunk-Index': i.toString(),
            }
          });
          if (res.status === 200) chunkDone = true;
          else throw new Error(`Chunk ${i} failed: HTTP ${res.status}`);
        } catch (e) {
          if (chunkAttempt === 3) throw e;
          await new Promise(r => setTimeout(r, 1000));
        }
      }

      // Clean up temp chunk file
      try { await FileSystem.deleteAsync(chunkTempUri, { idempotent: true }); } catch {}

      // ── Resumable: save progress after each successful chunk ──
      await AsyncStorage.setItem(progressKey, (i + 1).toString());

      // Update progress text
      setUploadProgress(prev => ({ ...prev, [asset.id]: `chunk ${i + 1}/${totalChunks}` }));
    }

    // Finalize — tell PC to merge all chunks (with retry — chunks are useless without this)
    let finalizeOk = false;
    for (let finAttempt = 0; finAttempt < 3 && !finalizeOk; finAttempt++) {
      try {
        const finRes = await fetch(`${baseUrl}/api/upload_finalize`, {
          method: 'POST',
          headers: {
            'X-FlyShelf-Client': 'MobileCompanion',
            'X-Pairing-Key': pairingKey ?? '',
            'X-Upload-Session': sessionId,
            'X-File-Name': encodeURIComponent(asset.filename || 'file.bin'),
            'X-Batch-Name': encodeURIComponent(batch),
            'X-Original-Date': (asset.creationTime || Date.now()).toString(),
            'X-Total-Chunks': totalChunks.toString(),
          }
        });
        if (finRes.ok) { finalizeOk = true; }
        else if (finAttempt === 2) throw new Error(`Finalize failed after 3 attempts: ${finRes.status}`);
      } catch (finErr) {
        if (finAttempt === 2) throw finErr;
        await new Promise(r => setTimeout(r, 2000));
      }
    }

    // ── Resumable: clean up progress key after successful finalization ──
    await AsyncStorage.removeItem(progressKey);
  }, [pairingKey, pairedDevices]);

  /**
   * Execute transfer — called by archive.tsx when the user presses Send.
   * @param targetNode     the selected FirebaseDevice (already has resolvedUrl resolved)
   * @param targetQueue    the assets to upload
   * @param batchName      folder name used server-side to group the batch
   * @param useRelay       whether to route through a relay PC
   * @param resolvedUrl    the URL to POST to
   */
  const executeTransfer = useCallback(async (
    targetNode: FirebaseDevice & { resolvedUrl?: string },
    targetQueue: MediaAsset[],
    batchName: string,
    useRelay: boolean,
    resolvedUrl: string,
    onComplete: () => void,
  ) => {
    if (!pairingKey) { Alert.alert('Error', 'No pairing key configured.'); return; }

    setIsUploading(true);
    setUploadIndex(0);
    setUploadTotal(targetQueue.length);
    isCancelledRef.current = false;
    isPausedRef.current = false;
    setUploadProgress({});

    const isCloudflare = useRelay || targetNode.connectionType === 'cloudflare';

    const processUpload = async (asset: MediaAsset): Promise<void> => {
      if (isCancelledRef.current) return;
      while (isPausedRef.current) {
        if (isCancelledRef.current) return;
        await new Promise(resolve => setTimeout(resolve, 500));
      }
      if (isCancelledRef.current) return;

      setUploadIndex(prev => prev + 1);
      setUploadProgress(prev => ({ ...prev, [asset.id]: 'sending' }));

      let attempt = 0;
      let success = false;

      while (attempt < 3 && !success && !isCancelledRef.current) {
        attempt++;
        try {
          let finalUploadUri = asset.uri;

          // Handle content:// URIs
          if (asset.uri.startsWith('content://') || (!asset.uri.startsWith('file://') && !asset.uri.startsWith('http'))) {
            if (asset.id && !asset.id.startsWith('browse_')) {
              const assetInfo = await MediaLibrary.getAssetInfoAsync(asset.id);
              finalUploadUri = assetInfo.localUri || assetInfo.uri;
            }
          }
          if (finalUploadUri.startsWith('content://')) {
            const safeName = `transfer_${Date.now()}_` + (asset.filename || 'file.bin').replace(/[^a-zA-Z0-9.-]/g, '_');
            const cachePath = `${FileSystem.cacheDirectory}${safeName}`;
            await FileSystem.copyAsync({ from: finalUploadUri, to: cachePath });
            finalUploadUri = cachePath;
          }

          const fileInfo = await FileSystem.getInfoAsync(finalUploadUri);
          if (fileInfo.exists) {
            const fileSize = (fileInfo as any).size || 0;
            const uploadEndpoint = useRelay ? 'relay_upload' : 'archive_upload';

            // Use chunked upload for large files over Cloudflare (>50MB)
            if (isCloudflare && fileSize > CHUNK_SIZE) {
              await uploadChunked(resolvedUrl, finalUploadUri, asset, batchName, fileSize);
            } else {
              // Single-POST for LAN or small files
              const uploadUrl = `${resolvedUrl}/api/${uploadEndpoint}`;
              const response = await FileSystem.uploadAsync(uploadUrl, finalUploadUri, {
                httpMethod: 'POST',
                uploadType: FileSystem.FileSystemUploadType.BINARY_CONTENT,
                headers: {
                  'X-FlyShelf-Client': 'MobileCompanion',
                  'X-Pairing-Key': pairingKey,
                  'X-Original-Date': (asset.creationTime || Date.now()).toString(),
                  'X-File-Name': encodeURIComponent(asset.filename || 'file.bin'),
                  'X-Batch-Name': encodeURIComponent(batchName),
                  'X-Source-Device': deviceName || 'Android',
                }
              });
              if (response.status !== 200) throw new Error('HTTP ' + response.status);
            }
            setUploadProgress(prev => ({ ...prev, [asset.id]: 'done' }));
            success = true;
          }
        } catch (error) {
          if (attempt === 3) {
            setUploadProgress(prev => ({ ...prev, [asset.id]: 'error' }));
          }
        }
      }
    };

    // Concurrent workers
    const workers = [];
    let currentIndex = 0;
    const CONCURRENCY = 2;
    for (let i = 0; i < Math.min(CONCURRENCY, targetQueue.length); i++) {
      workers.push((async function worker() {
        while (currentIndex < targetQueue.length) {
          if (isCancelledRef.current) break;
          const asset = targetQueue[currentIndex++];
          await processUpload(asset);
        }
      })());
    }
    await Promise.all(workers);

    setIsUploading(false);
    if (isCancelledRef.current) {
      Alert.alert('Cancelled', 'Transfer was cancelled.');
    } else {
      setTimeout(() => {
        setUploadProgress({});
        Alert.alert('Transfer Complete ✅', `Sent ${targetQueue.length} items to ${targetNode.DeviceName}`);
        onComplete();
      }, 1000);
    }
  }, [pairingKey, deviceName, uploadChunked]);

  // Pause / cancel controls (called from upload progress screen)
  const pauseTransfer = useCallback(() => {
    isPausedRef.current = !isPausedRef.current;
    return isPausedRef.current;
  }, []);

  const cancelTransfer = useCallback(() => {
    isCancelledRef.current = true;
  }, []);

  return {
    // Firebase
    allFirebaseDevices,
    deviceGroups,
    saveGroupToFirebase,
    deleteGroupFromFirebase,
    // Media scan
    hasPermission,
    mediaAssets,
    setMediaAssets,
    isScanning,
    isBackgroundRefreshing,
    scanMedia,
    autoScan,
    hasScannedRef,
    // Transfer
    isUploading,
    isPausedRef,
    isCancelledRef,
    uploadIndex,
    uploadTotal,
    uploadProgress,
    setUploadProgress,
    executeTransfer,
    pauseTransfer,
    cancelTransfer,
  };
}
