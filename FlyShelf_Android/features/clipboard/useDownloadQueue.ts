import React, { useRef, useCallback } from 'react';
import { Platform, ToastAndroid } from 'react-native';
import { toast } from '../../context/ToastContext';
import * as FileSystem from 'expo-file-system/legacy';
import * as Notifications from 'expo-notifications';
import { ref, set, get } from 'firebase/database';
import { database } from '../../firebaseConfig';
import { ClipItem, SYNC_CACHE_BASE } from '../../utils/clipTypes';
import { syncLog } from '../../utils/debugLog';
import { NetworkClock } from '../../utils/networkClock';

// ─── Download Queue: Sequential file download processor ───
export interface DownloadQueueItem {
  id: string;
  title: string;
  type: string;
  fileUrl: string;
  destPath: string;
  source: string;
  sourceDevice: string;
  retryCount?: number;
  timestamp?: number;
  expectedSize?: number;
}

export function useDownloadQueue(params: {
  pairingKeyRef: React.MutableRefObject<string>;
  deviceName: string;
  getCachedPcUrl: () => Promise<string>;
  setClips: React.Dispatch<React.SetStateAction<ClipItem[]>>;
  setDownloadedItems: React.Dispatch<React.SetStateAction<Set<string>>>;
}): {
  enqueueDownload: (item: DownloadQueueItem) => void;
  markFileDownloaded: (entryId: string) => Promise<void>;
} {
  const { pairingKeyRef, deviceName, getCachedPcUrl, setClips, setDownloadedItems } = params;

  const downloadQueueRef = useRef<DownloadQueueItem[]>([]);
  const isDownloadingRef = useRef<boolean>(false);
  // H-4 FIX: Use Map with timestamps instead of Set for reliable chronological eviction
  const processedDownloadsRef = useRef<Map<string, number>>(new Map());

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
        if (existing.exists && ('size' in existing && typeof (existing as any).size === 'number' && (existing as any).size > 100)) {
          syncLog('DL-QUEUE', `Skip (exists): ${item.title}`);
          setDownloadedItems(prev => { const n = new Set(prev); n.add(item.id || item.title); return n; });
          // Update clip with CachedUri
          setClips(prev => prev.map(c =>
            (c.id === item.id || c.Title === item.title) ? { ...c, CachedUri: item.destPath } : c
          ));
          continue;
        }

        // Show progress card
        const isLargeFile = (item.expectedSize || 0) > 10 * 1024 * 1024; // >10MB
        setClips(prev => [{
          id: progressId, Title: `⬇️ ${item.title}`, Type: '_DownloadProgress',
          Raw: isLargeFile
            ? `Downloading from ${item.sourceDevice}... 0%`
            : `Downloading from ${item.sourceDevice} via ${item.source}...`,
          Time: new Date().toLocaleTimeString(),
          _isTransient: true,
          _downloadProgress: 0,
          _downloadSpeed: '',
          _downloadSize: item.expectedSize || 0,
        } as any, ...prev]);

        syncLog('DL-QUEUE', `Downloading: ${item.title} via ${item.source}${isLargeFile ? ` (${((item.expectedSize || 0) / 1024 / 1024).toFixed(1)}MB)` : ''}`);
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
            // Scale timeout: minimum 60s, +1s per 256KB expected, capped at 600s
            const dlTimeoutMs = Math.max(60000, Math.min(600000, ((item as any).expectedSize || 0) / 256));

            // Progress tracking for large files
            let dlStartTime = Date.now();
            let lastProgressUpdate = 0;
            const progressCallback = isLargeFile ? (downloadProgress: { totalBytesWritten: number; totalBytesExpectedToWrite: number }) => {
              const now = Date.now();
              // Throttle updates to every 500ms to avoid excessive re-renders
              if (now - lastProgressUpdate < 500) return;
              lastProgressUpdate = now;

              const { totalBytesWritten, totalBytesExpectedToWrite } = downloadProgress;
              const totalBytes = totalBytesExpectedToWrite > 0 ? totalBytesExpectedToWrite : (item.expectedSize || 0);
              const pct = totalBytes > 0 ? Math.round((totalBytesWritten / totalBytes) * 100) : 0;
              const elapsed = (now - dlStartTime) / 1000; // seconds
              const speedMBps = elapsed > 0 ? (totalBytesWritten / 1024 / 1024) / elapsed : 0;
              const speedStr = speedMBps >= 1
                ? `${speedMBps.toFixed(1)} MB/s`
                : `${(speedMBps * 1024).toFixed(0)} KB/s`;
              const downloadedMB = (totalBytesWritten / 1024 / 1024).toFixed(1);
              const totalMB = totalBytes > 0 ? (totalBytes / 1024 / 1024).toFixed(1) : '?';

              setClips(prev => prev.map(c =>
                c.id === progressId
                  ? {
                      ...c,
                      Raw: `${downloadedMB} / ${totalMB} MB • ${speedStr} • ${pct}%`,
                      _downloadProgress: pct / 100,
                      _downloadSpeed: speedStr,
                    } as any
                  : c
              ));
            } : undefined;

            // Use DownloadResumable for cancellable downloads with progress
            const resumable = FileSystem.createDownloadResumable(
              currentFileUrl,
              item.destPath,
              { headers: dlHeaders },
              progressCallback,
            );
            const timeoutId = setTimeout(async () => {
              try { await resumable.cancelAsync(); } catch (e) { /* ignore */ }
            }, dlTimeoutMs);
            let dlResult;
            try {
              dlResult = await resumable.downloadAsync();
              clearTimeout(timeoutId);
            } catch (e) {
              clearTimeout(timeoutId);
              throw e;
            }

            if (dlResult && dlResult.status === 200) {
              queueDlSuccess = true;
              // L-6: Log content type for debugging mismatches
              const contentType = dlResult.headers?.['Content-Type'] || dlResult.headers?.['content-type'] || 'unknown';
              syncLog('DL-QUEUE', `Content-Type: ${contentType} for ${item.title}`);
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
          processedDownloadsRef.current.set(dedupKey, Date.now()); // H-4: Store with timestamp
          syncLog('DL-QUEUE', `✅ ${item.title} saved via ${item.source}`);
          setDownloadedItems(prev => { const n = new Set(prev); n.add(item.id || item.title); return n; });
          // Update clip with CachedUri
          setClips(prev => prev.map(c =>
            (c.id === item.id || c.Title === item.title) ? { ...c, CachedUri: item.destPath } : c
          ));
          toast.success('File Downloaded', `${item.title} saved to offline storage`);
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
    // H-4 FIX: Timestamp-based eviction — remove oldest entries first
    if (processedDownloadsRef.current.size > 500) {
      const entries = Array.from(processedDownloadsRef.current.entries());
      entries.sort((a, b) => a[1] - b[1]); // Sort by timestamp ascending
      const toRemove = entries.slice(0, entries.length - 200); // Keep 200 newest
      for (const [key] of toRemove) {
        processedDownloadsRef.current.delete(key);
      }
    }
  }, []);

  // ─── downloadedBy Tracking: Mark file as downloaded by this device ───
  const markFileDownloaded = async (entryId: string) => {
    try {
      if (!entryId) return;
      setDownloadedItems(prev => {
        const next = new Set(prev);
        next.add(entryId);
        return next;
      });
      syncLog('[SYNC_TRACK]', `Marked ${entryId} as downloaded locally`);
    } catch (e) { syncLog('[SYNC_TRACK]', `markFileDownloaded error: ${e}`); }
  };

  const enqueueDownload = useCallback((item: DownloadQueueItem) => {
    // Dedup: don't re-enqueue already-processed or already-queued items
    const dedupKey = `${item.title}::${item.timestamp || item.fileUrl}`;
    if (processedDownloadsRef.current.has(dedupKey)) return; // H-4: Map.has() works the same
    if (downloadQueueRef.current.some(q => `${q.title}::${q.timestamp || q.fileUrl}` === dedupKey)) return;
    downloadQueueRef.current.push(item);
    processDownloadQueue();
  }, [processDownloadQueue]);

  return { enqueueDownload, markFileDownloaded };
}
