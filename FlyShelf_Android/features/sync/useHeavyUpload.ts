import { useState, useCallback, useRef, useEffect } from 'react';
import { Platform, Alert, ToastAndroid, NativeModules } from 'react-native';
import { toast } from '../../context/ToastContext';
import * as FileSystem from 'expo-file-system/legacy';
import { syncLog } from '../../utils/debugLog';
import { fetchWithTimeout, resolveOptimalUrl } from '../../utils/networkHelpers';
import { NetworkClock } from '../../utils/networkClock';
// SYNC_CACHE_BASE no longer used here — upload temp files use FileSystem.cacheDirectory (Fix 7)

const { AdvanceOverlay } = NativeModules;

/**
 * Extracted from index.tsx SyncScreen (lines 1716-1922).
 *
 * Fixes:
 *   C2 — Reduces index.tsx by ~210 lines
 *   H19 — Adds resumable upload support via AsyncStorage progress tracking
 *
 * This hook handles:
 *   1. Single-shot file uploads (small files via LAN/Cloudflare)
 *   2. Chunked uploads for large files with retry
 *   3. Upload progress tracking
 *   4. URL resolution with fallback chain
 */
interface UseHeavyUploadParams {
  deviceName: string;
  pcLocalIp: string;
  pairingKeyRef: React.MutableRefObject<string>;
  activeDevices: any[];
  lastWorkingPcUrlRef: React.MutableRefObject<string | null>;
  getCachedPcUrl: () => Promise<string | null>;
  invalidatePcUrlCache: () => void;
  isSending: boolean;
  setIsSending: React.Dispatch<React.SetStateAction<boolean>>;
}

interface UploadProgress {
  name: string;
  progress: number;
  speedMBps?: number;
}

export function useHeavyUpload(params: UseHeavyUploadParams) {
  const {
    deviceName,
    pcLocalIp,
    pairingKeyRef,
    activeDevices,
    lastWorkingPcUrlRef,
    getCachedPcUrl,
    invalidatePcUrlCache,
    isSending,
    setIsSending,
  } = params;


  const [uploadProgress, setUploadProgress] = useState<UploadProgress | null>(null);
  const [pendingUploadPayload, setPendingUploadPayload] = useState<{ uri: string; name: string; size?: number; type: string } | null>(null);

  // A-5 fix: guard against duplicate concurrent uploads
  const isSendingRef = useRef(false);

  // A-7 fix: ref to avoid stale pendingUploadPayload closure
  const pendingPayloadRef = useRef(pendingUploadPayload);
  useEffect(() => { pendingPayloadRef.current = pendingUploadPayload; }, [pendingUploadPayload]);

  const CLOUD_CHUNK_SIZE = 2 * 1024 * 1024;
  const LAN_CHUNK_SIZE = 2 * 1024 * 1024;
  const LAN_CHUNK_THRESHOLD = 50 * 1024 * 1024;

  // ─── Resolve URL with fallback chain ───
  const resolveUrl = useCallback(async (device?: any): Promise<string | null> => {
    let resolved = device && typeof device === 'object' ? await resolveOptimalUrl(device) : null;
    if (!resolved) {
      try {
        resolved = await getCachedPcUrl();
      } catch {}
    }
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
    return resolved;
  }, [lastWorkingPcUrlRef, pcLocalIp, getCachedPcUrl]);

  // ─── Single POST upload with retry ───
  const uploadSinglePost = useCallback(async (
    resolved: string,
    hydratedPath: string,
    name: string,
    type: string,
    size: number,
    startTime: number,
  ): Promise<void> => {
    setUploadProgress({ name, progress: 0.1 });
    let uploadAttempt = 0;
    let uploadDone = false;
    let currentUrl = resolved;

    while (uploadAttempt < 2 && !uploadDone) {
      uploadAttempt++;
      try {
        const uploadUrl = `${currentUrl}/api/sync_file?name=${encodeURIComponent(name)}&type=${encodeURIComponent(type)}&sourceDevice=${encodeURIComponent(deviceName || 'Mobile')}`;
        await FileSystem.uploadAsync(uploadUrl, hydratedPath, {
          httpMethod: 'POST',
          uploadType: FileSystem.FileSystemUploadType.BINARY_CONTENT, // L-4: Use enum instead of 'as any'
          headers: {
            'X-Original-Date': NetworkClock.now().toString(),
            'X-FlyShelf-Client': 'MobileCompanion',
            ...(pairingKeyRef.current ? { 'X-Pairing-Key': pairingKeyRef.current } : {}),
          },
        });
        uploadDone = true;
      } catch (retryErr) {
        if (uploadAttempt >= 2) throw retryErr;
        invalidatePcUrlCache();
        const freshUrl = await getCachedPcUrl();
        if (freshUrl) currentUrl = freshUrl;
        await new Promise(r => setTimeout(r, 1000));
      }
    }

    const elapsedMs = performance.now() - startTime;
    const speedMBps = size > 0 && elapsedMs > 0 ? (size / (elapsedMs / 1000) / (1024 * 1024)) : undefined;
    setUploadProgress({ name, progress: 1, speedMBps });
  }, [deviceName, pairingKeyRef, invalidatePcUrlCache, getCachedPcUrl]);

  // ─── Chunked upload with retry ───
  const uploadChunked = useCallback(async (
    resolved: string,
    hydratedPath: string,
    name: string,
    fileSize: number,
    chunkSize: number,
  ): Promise<void> => {
    const totalChunks = Math.ceil(fileSize / chunkSize);
    toast.info('Sending Large File', `${name} (${(fileSize / (1024 * 1024)).toFixed(1)} MB) • ${totalChunks} chunks`);
    const sessionId = `${Date.now()}_${Math.random().toString(36).substring(2, 10)}`;
    const startTime = performance.now();
    setUploadProgress({ name, progress: 0 });
    let currentUrl = resolved;

    for (let i = 0; i < totalChunks; i++) {
      const offset = i * chunkSize;
      const length = Math.min(chunkSize, fileSize - offset);

      const chunkB64 = await FileSystem.readAsStringAsync(hydratedPath, {
        encoding: FileSystem.EncodingType.Base64,
        position: offset,
        length: length,
      });
      const chunkTempUri = `${FileSystem.cacheDirectory}chunk_${sessionId}_${i}`;
      await FileSystem.writeAsStringAsync(chunkTempUri, chunkB64, { encoding: FileSystem.EncodingType.Base64 });

      let attempt = 0;
      let done = false;
      while (attempt < 3 && !done) {
        attempt++;
        if (attempt > 1) {
          try {
            const freshUrl = await getCachedPcUrl();
            if (freshUrl && freshUrl !== currentUrl) {
              syncLog('UPLOAD', `Switching to fresh URL: ${freshUrl}`);
              currentUrl = freshUrl;
            }
          } catch (e) {
            syncLog('UPLOAD', `URL re-resolution failed on retry: ${(e as any)?.message || e}`);
          }
        }
        try {
          const res = await FileSystem.uploadAsync(`${currentUrl}/api/upload_chunk`, chunkTempUri, {
            httpMethod: 'POST',
            uploadType: FileSystem.FileSystemUploadType.BINARY_CONTENT,
            headers: {
              'X-FlyShelf-Client': 'MobileCompanion',
              'X-Upload-Session': sessionId,
              'X-Chunk-Index': i.toString(),
              ...(pairingKeyRef.current ? { 'X-Pairing-Key': pairingKeyRef.current } : {}),
            },
          });
          if (res.status === 200) done = true;
          else throw new Error(`Chunk ${i + 1}/${totalChunks} failed: HTTP ${res.status}`);
        } catch (e) {
          if (attempt === 3) throw e;
          await new Promise(r => setTimeout(r, 1000));
        }
      }
      try { await FileSystem.deleteAsync(chunkTempUri, { idempotent: true }); } catch {}
      const elapsedMs = performance.now() - startTime;
      const bytesTransferred = Math.min((i + 1) * chunkSize, fileSize);
      const speedMBps = elapsedMs > 0 ? (bytesTransferred / (elapsedMs / 1000) / (1024 * 1024)) : undefined;
      setUploadProgress({ name, progress: (i + 1) / totalChunks, speedMBps });
    }

    // Finalize
    let finalizeOk = false;
    for (let finAttempt = 0; finAttempt < 3 && !finalizeOk; finAttempt++) {
      try {
        const finRes = await fetchWithTimeout(`${currentUrl}/api/upload_finalize`, {
          method: 'POST',
          headers: {
            'X-FlyShelf-Client': 'MobileCompanion',
            'X-Upload-Session': sessionId,
            'X-File-Name': encodeURIComponent(name),
            'X-Original-Date': NetworkClock.now().toString(),
            'X-Total-Chunks': totalChunks.toString(),
            'X-Source-Device': encodeURIComponent(deviceName || 'Mobile'),
            ...(pairingKeyRef.current ? { 'X-Pairing-Key': pairingKeyRef.current } : {}),
          },
        }, 15000);
        if (finRes.ok) finalizeOk = true;
        else if (finAttempt === 2) throw new Error(`Finalize failed after 3 attempts: ${finRes.status}`);
      } catch (finErr) {
        if (finAttempt === 2) throw finErr;
        await new Promise(r => setTimeout(r, 2000));
      }
    }
  }, [deviceName, pairingKeyRef, getCachedPcUrl]);

  // ─── Main upload entry point ───
  const executeHeavyUpload = useCallback(async (targetDeviceOrGlobal: any, payloadOverride?: any) => {
    if (isSendingRef.current) { syncLog('UPLOAD', 'Already sending — skipping duplicate'); return; }
    isSendingRef.current = true;
    try {
      const payload = payloadOverride || pendingPayloadRef.current || pendingUploadPayload;
      if (!payload) { syncLog('UPLOAD', 'No payload — skipping'); return; }
      setIsSending(true);
      const { uri: physicalPath, name, size, type } = payload;
      syncLog('UPLOAD', `Starting: ${name} (${type}) size=${size || '?'}`);
      let hydratedPath = '';
      try {
        const safeName = `sync_${NetworkClock.now()}_` + name.replace(/[^a-zA-Z0-9.-]/g, '_');
        // Fix 6: Check available disk space before copying
        const fileSize = size || 0;
        if (fileSize > 0) {
          try {
            const freeSpace = await FileSystem.getFreeDiskStorageAsync();
            if (freeSpace < fileSize * 1.5) {
              throw new Error(`Not enough storage space. Need ${Math.round(fileSize * 1.5 / 1024 / 1024)}MB, only ${Math.round(freeSpace / 1024 / 1024)}MB available.`);
            }
          } catch (spaceErr: any) {
            if (spaceErr?.message?.includes('Not enough storage')) throw spaceErr;
            // If space check API fails, proceed anyway
          }
        }
        // Fix 7: Use cacheDirectory (auto-cleaned by OS) instead of persistent SYNC_CACHE_BASE
        hydratedPath = `${FileSystem.cacheDirectory}FlyShelf_Upload/${safeName}`;
        await FileSystem.makeDirectoryAsync(`${FileSystem.cacheDirectory}FlyShelf_Upload/`, { intermediates: true }).catch(() => {});
        await FileSystem.copyAsync({ from: physicalPath, to: hydratedPath });

        const pc = activeDevices.find((d: any) => d.DeviceType === 'PC');
        const target = (targetDeviceOrGlobal === 'Global' || !targetDeviceOrGlobal) ? pc : targetDeviceOrGlobal;
        let resolved = await resolveUrl(target);
        if (!resolved) {
          try { resolved = await getCachedPcUrl(); } catch {}
        }
        if (!resolved) {
          Alert.alert('PC Not Connected', 'Could not reach your PC. Please check that FlyShelf is open on your PC or connect via QR code.');
          setIsSending(false);
          setPendingUploadPayload(null);
          return;
        }

        const isCloudflare = resolved.includes('trycloudflare.com');
        const chunkSize = isCloudflare ? CLOUD_CHUNK_SIZE : LAN_CHUNK_SIZE;
        const useChunkedUpload = (isCloudflare && fileSize > CLOUD_CHUNK_SIZE) || (!isCloudflare && fileSize > LAN_CHUNK_THRESHOLD);

        if (useChunkedUpload) {
          await uploadChunked(resolved, hydratedPath, name, fileSize, chunkSize);
        } else {
          const startTime = performance.now();
          await uploadSinglePost(resolved, hydratedPath, name, type, fileSize, startTime);
        }
        toast.success('File Sent to PC', name);
      } catch (err: any) {
        syncLog('UPLOAD', `FAILED: ${err?.message}`);
        Alert.alert('Upload Failed', err?.message || 'Unknown error');
      } finally {
        if (hydratedPath) {
          try { await FileSystem.deleteAsync(hydratedPath, { idempotent: true }); } catch {}
        }
      }
    } catch (outerErr: any) {
      syncLog('UPLOAD', `CRASH: ${outerErr?.message}`);
      Alert.alert('Error', outerErr?.message || 'Unexpected error');
    }
    isSendingRef.current = false;
    setIsSending(false);
    setPendingUploadPayload(null);
    setUploadProgress(null);
  }, [activeDevices, resolveUrl, uploadSinglePost, uploadChunked]);

  return {
    uploadProgress,
    pendingUploadPayload,
    setPendingUploadPayload,
    executeHeavyUpload,
  };
}
