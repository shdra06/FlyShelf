import React, { useRef, useEffect } from 'react';
import { Platform, ToastAndroid, NativeModules } from 'react-native';
import * as FileSystem from 'expo-file-system/legacy';
import * as Clipboard from 'expo-clipboard';
import { ClipItem, SYNC_CACHE_BASE } from '../../utils/clipTypes';
import { syncLog } from '../../utils/debugLog';

const { AdvanceOverlay } = NativeModules;

// ─── Background image download sweep ───
// Watches clips for image items that need downloading (added by LAN poll or Firebase)
// Decouples detection from download — any path can add images, this path downloads them
export function useImageSweep(params: {
  imageDownloadTrigger: number;
  clipsStateRef: React.MutableRefObject<ClipItem[]>;
  pairingKeyRef: React.MutableRefObject<string>;
  lastWorkingPcUrlRef: React.MutableRefObject<string | null>;
  activeDevicesRef: React.MutableRefObject<any[]>;
  getCachedPcUrl: () => Promise<string>;
  isFloatingBallEnabled: boolean;
  setClips: React.Dispatch<React.SetStateAction<ClipItem[]>>;
}): void {
  const {
    imageDownloadTrigger,
    clipsStateRef,
    pairingKeyRef,
    lastWorkingPcUrlRef,
    activeDevicesRef,
    getCachedPcUrl,
    isFloatingBallEnabled,
    setClips,
  } = params;

  // downloadingRef lives inside this hook — tracks in-flight image downloads
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
          if (existing.exists && ('size' in existing && typeof (existing as any).size === 'number' && (existing as any).size > 100)) {
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
                if (info.exists && ('size' in info && typeof (info as any).size === 'number' && (info as any).size > 100)) {
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
        // AH-1: Always clean up downloadingRef in finally block
        } finally {
          downloadingRef.current.delete(imgItem.id!);
        }
      }
    })();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [imageDownloadTrigger]);
}
