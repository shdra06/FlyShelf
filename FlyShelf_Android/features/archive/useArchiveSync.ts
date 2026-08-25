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
 * The UI pieces (rendering, selection, modals) remain in archive.tsx.
 */

import { useState, useEffect, useRef, useCallback } from 'react';
import { Platform, Alert, ToastAndroid } from 'react-native';
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
        const allDevs = await decryptDeviceList(filtered as any);
        setAllFirebaseDevices(allDevs as any);
      } else {
        setAllFirebaseDevices([]);
      }
    });
    return () => unsubscribeNodes();
  }, [pairingKey]);

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
  // Media scan — uses native MediaStore for instant document discovery
  // ═══════════════════════════════════════════════════════════
  const scanMedia = useCallback(async (startDate: Date, endDate: Date) => {
    if (hasPermission === false) {
      if (Platform.OS === 'android') ToastAndroid.show('Permission denied — enable storage access in Settings', ToastAndroid.LONG);
      return;
    }
    setIsScanning(true);
    setMediaAssets([]);
    if (Platform.OS === 'android') ToastAndroid.show('🔍 Scanning files...', ToastAndroid.SHORT);

    try {
      let allFound: any[] = [];

      // 1. Gallery scan for images/videos with date filter (via expo-media-library)
      try {
        let hasNextPage = true;
        let after = undefined;
        while (hasNextPage) {
          let media = await MediaLibrary.getAssetsAsync({
            first: 100,
            after: after,
            mediaType: ['photo', 'video'],
            createdAfter: startDate.getTime(),
            createdBefore: endDate.getTime(),
            sortBy: [[MediaLibrary.SortBy.creationTime, false]]
          });
          allFound = [...allFound, ...media.assets.map(a => ({ ...a, source: 'Camera' }))];
          hasNextPage = media.hasNextPage;
          after = media.endCursor;
        }
      } catch (mediaErr: any) {
        console.warn('MediaLibrary scan failed:', mediaErr?.message);
      }

      // 2. Native Document Scanner (Disabled as module is missing — using fallback scan instead)
      let nativeScanWorked = false;

      // 3. Always run fallback filesystem scan to supplement (catches files MediaStore might miss)
      if (!nativeScanWorked) {
        await fallbackFileScan(allFound);
      }

      const uniqueAssets = Array.from(new Map(allFound.map(item => [item.id, item])).values());
      uniqueAssets.sort((a, b) => b.creationTime - a.creationTime);
      setMediaAssets(uniqueAssets);

      if (Platform.OS === 'android') {
        const imgCount = uniqueAssets.filter(a => a.mediaType === 'photo').length;
        const vidCount = uniqueAssets.filter(a => a.mediaType === 'video').length;
        const docCount = uniqueAssets.filter(a => a.mediaType === 'pdf' || a.mediaType === 'doc').length;
        ToastAndroid.show(`✅ ${imgCount} images, ${vidCount} videos, ${docCount} docs`, ToastAndroid.LONG);
      }
    } catch (e) { console.error(e); }
    setIsScanning(false);
  }, [hasPermission]);

  // Auto-scan when permission is available and haven't scanned yet
  const autoScan = useCallback((startDate: Date, endDate: Date) => {
    if (hasPermission && !hasScannedRef.current && mediaAssets.length === 0 && !isScanning) {
      hasScannedRef.current = true;
      scanMedia(startDate, endDate);
    }
  }, [hasPermission, mediaAssets.length, isScanning, scanMedia]);

  // ─── Fallback filesystem scan ────────────────────────────────
  const fallbackFileScan = async (allFound: any[]) => {
    if (Platform.OS === 'web') return;
    const scanRoots: { path: string; source: SourceFilter; recursive?: boolean }[] = [
      { path: 'file:///storage/emulated/0/Download/', source: 'Downloads', recursive: true },
      { path: 'file:///storage/emulated/0/Documents/', source: 'Downloads', recursive: true },
      { path: 'file:///storage/emulated/0/WhatsApp/Media/WhatsApp Documents/', source: 'WhatsApp', recursive: true },
      { path: 'file:///storage/emulated/0/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Documents/', source: 'WhatsApp', recursive: true },
    ];

    const scanDir = async (dirPath: string, source: SourceFilter, depth: number = 0, maxDepth: number = 2) => {
      if (depth > maxDepth) return;
      try {
        const check = await FileSystem.getInfoAsync(dirPath);
        if (!check.exists || !check.isDirectory) return;
        const files = await FileSystem.readDirectoryAsync(dirPath);
        for (const file of files) {
          if (file === '.nomedia' || file.startsWith('.') || file === 'Android' || file === 'node_modules') continue;
          const fullPath = dirPath + file;
          try {
            const fInfo = await FileSystem.getInfoAsync(fullPath);
            if (fInfo.exists && fInfo.isDirectory && depth < maxDepth) {
              await scanDir(fullPath + '/', source, depth + 1, maxDepth);
            } else if (fInfo.exists && !fInfo.isDirectory) {
              const lowerFile = file.toLowerCase();
              let mediaType = '';
              if (lowerFile.endsWith('.pdf')) mediaType = 'pdf';
              else if (lowerFile.match(/\.(doc|docx|txt|xlsx|xls|pptx|ppt|odt|rtf|csv)$/)) mediaType = 'doc';
              else if (lowerFile.match(/\.(apk|zip|rar|7z|tar|gz)$/)) mediaType = 'doc';
              if (!mediaType) continue;
              const rawModTime = fInfo.modificationTime || 0;
              // Sanity check: if modificationTime is already in ms (>1e12), don't multiply
              const modTimeMs = rawModTime > 1e12 ? rawModTime : rawModTime * 1000;
              allFound.push({ id: fullPath, uri: fullPath, filename: file, creationTime: modTimeMs, mediaType, source, fileSize: (fInfo as any).size || 0 });
            }
          } catch {}
        }
      } catch {}
    };

    for (const { path, source, recursive } of scanRoots) {
      await scanDir(path, source, 0, recursive ? 4 : 0);
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
    const sessionId = `${Date.now()}_${Math.random().toString(36).substring(2, 10)}`;
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
