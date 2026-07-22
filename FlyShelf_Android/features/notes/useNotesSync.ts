/**
 * useNotesSync.ts
 * ────────────────────────────────────────────────────────────────
 * Extracted from notes.tsx — owns all PC-polling / debounced-POST
 * sync logic for the Notes screen.
 *
 * Responsibilities:
 *   • Load cached NoteDay[] from AsyncStorage on mount
 *   • Resolve the best PC URL via resolveBestPcUrl
 *   • Poll /api/notes every POLL_INTERVAL ms (GET) and merge remote data
 *   • Debounce POST of locally-modified days back to the PC
 *   • Maintain an offline queue (PENDING_NOTES_SYNC_KEY) for retry
 *
 * Returns:
 *   days            – merged NoteDay array (source of truth for UI)
 *   setDays         – raw setter (UI can call it directly for local edits)
 *   syncStatus      – 'synced' | 'syncing' | 'offline'
 *   isLoading       – true while the initial AsyncStorage load is running
 *   modifiedDatesRef – Set<string> of locally-edited date keys the hook
 *                      must POST; add to it before calling schedulePost()
 *   schedulePost    – debounce-trigger: call after every local edit
 *   schedulePostRef – stable ref to schedulePost (avoids stale closures)
 *   daysRef         – stable ref to days array (avoids stale closures)
 *   NOTES_STORAGE_KEY – exported so callers can persist edits via the same key
 */

import { useState, useEffect, useRef, useCallback } from 'react';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { Platform, ToastAndroid } from 'react-native';

import { useSettings } from '../../context/SettingsContext';
import { fetchWithTimeout, resolveBestPcUrl } from '../../utils/networkHelpers';
import { NoteDay, NoteBullet } from '../../utils/noteTypes';

// ─── Constants (mirrored from notes.tsx) ───────────────────────
export const NOTES_STORAGE_KEY = '@flyshelf_notes';
const PENDING_NOTES_SYNC_KEY = '@flyshelf_pending_notes_sync';
const POLL_INTERVAL = 10_000;
const DEBOUNCE_POST_MS = 2_000;

// ─── Helpers ───────────────────────────────────────────────────
/** Android-only toast; silently no-ops on other platforms. */
const showToast = (msg: string) => {
  if (Platform.OS === 'android') {
    ToastAndroid.show(msg, ToastAndroid.SHORT);
  }
};

// A-16 FIX: Shared helper for offline queue persist with simple lock
let isQueueingDates = false;
const queueFailedDates = async (failedDates: string[]) => {
  if (isQueueingDates) return; // Simple lock to prevent interleaving
  isQueueingDates = true;
  try {
    const stored = await AsyncStorage.getItem(PENDING_NOTES_SYNC_KEY);
    const existing: string[] = stored ? JSON.parse(stored) : [];
    const mergedDates = [...new Set([...existing, ...failedDates])];
    await AsyncStorage.setItem(PENDING_NOTES_SYNC_KEY, JSON.stringify(mergedDates));
  } catch {} // Best-effort
  finally { isQueueingDates = false; }
};

// ─── Hook ──────────────────────────────────────────────────────
export function useNotesSync() {
  const { pcLocalIp, pairingKey, pairedDevices, deviceName, getSyncPrefsForDevice } = useSettings();

  // ─── State ───
  const [days, setDays] = useState<NoteDay[]>([]);
  const [syncStatus, setSyncStatus] = useState<'synced' | 'syncing' | 'offline'>('offline');
  const [isLoading, setIsLoading] = useState(true);

  // ─── Refs ───
  const debounceTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const pollTimerRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const pcUrlRef = useRef<string | null>(null);
  const daysRef = useRef<NoteDay[]>([]);
  /** Date keys of locally-modified days that need to be POSTed. */
  const modifiedDatesRef = useRef<Set<string>>(new Set());
  const syncFailCountRef = useRef(0);
  const notesPollFailCountRef = useRef(0);
  const mountedRef = useRef(true);
  const schedulePostRef = useRef<(() => void) | null>(null);
  // A-8 fix: persist lock to prevent concurrent AsyncStorage writes
  const isPersistingRef = useRef(false);

  // Refs to avoid stale closures in fetchRemoteNotes
  const pairedDevicesRef = useRef(pairedDevices);
  useEffect(() => { pairedDevicesRef.current = pairedDevices; }, [pairedDevices]);
  const pcLocalIpRef = useRef(pcLocalIp);
  useEffect(() => { pcLocalIpRef.current = pcLocalIp; }, [pcLocalIp]);

  // A-9 fix: ref for getSyncPrefsForDevice to avoid stale closure in fetchRemoteNotes
  const getSyncPrefsRef = useRef(getSyncPrefsForDevice);
  useEffect(() => { getSyncPrefsRef.current = getSyncPrefsForDevice; }, [getSyncPrefsForDevice]);

  // Keep daysRef in sync with state
  useEffect(() => { daysRef.current = days; }, [days]);

  // Unmount guard + debounce cleanup
  useEffect(() => {
    return () => {
      mountedRef.current = false;
      if (debounceTimerRef.current) clearTimeout(debounceTimerRef.current);
    };
  }, []);

  // ═══════════════════════════════════════════════════════════
  // SYNC: Load cached notes on mount
  // ═══════════════════════════════════════════════════════════
  useEffect(() => {
    (async () => {
      try {
        const stored = await AsyncStorage.getItem(NOTES_STORAGE_KEY);
        if (stored) {
          const parsed: NoteDay[] = JSON.parse(stored);
          if (Array.isArray(parsed) && parsed.length > 0) {
            setDays(parsed);
          }
        }
      } catch (e) { console.warn('[Notes] Failed to load cached notes:', e); }

      setIsLoading(false);
    })();
  }, []);

  // ═══════════════════════════════════════════════════════════
  // SYNC: Poll for remote notes (GET /api/notes)
  // ═══════════════════════════════════════════════════════════
  const fetchRemoteNotes = useCallback(async () => {
    // Resolve PC URL from global context (discovered by main tab)
    // Uses refs to avoid stale closure — pairedDevicesRef/pcLocalIpRef stay current
    let pcUrl = resolveBestPcUrl(pairedDevicesRef.current, pcLocalIpRef.current);
    // Fallback: if resolveBestPcUrl returns null but we have a manual IP, use it directly
    // The main tab's clipboard sync may already be working over this IP
    if (!pcUrl && pcLocalIpRef.current) {
      const ip = pcLocalIpRef.current.trim();
      if (ip) {
        pcUrl = ip.startsWith('http') ? ip.replace(/\/$/, '') : `http://${ip.includes(':') ? ip : ip + ':8999'}`;
      }
    }
    if (pcUrl) pcUrlRef.current = pcUrl;

    if (!pcUrlRef.current || !pairingKey) {
      setSyncStatus('offline');
      return;
    }

    // Gate: skip sync if no paired device has notes sync enabled
    const anyDeviceWantsNotes =
      pairedDevicesRef.current.length === 0 ||
      pairedDevicesRef.current.some(d => getSyncPrefsRef.current(d.deviceId).notes);
    if (!anyDeviceWantsNotes) {
      setSyncStatus('offline');
      return;
    }

    try {
      setSyncStatus('syncing');
      // Snapshot modified dates before fetch to avoid race condition
      const dirtySnapshot = new Set(modifiedDatesRef.current);
      const res = await fetchWithTimeout(`${pcUrlRef.current}/api/notes`, {
        method: 'GET',
        headers: {
          'X-FlyShelf-Client': 'MobileCompanion',
          'X-Pairing-Key': pairingKey,
        },
      }, 5000);

      if (!res.ok) {
        setSyncStatus('offline');
        syncFailCountRef.current++;
        notesPollFailCountRef.current++;
        if (syncFailCountRef.current === 2) {
          showToast('Notes sync offline — PC may be unreachable');
        }
        return;
      }

      const remoteDays: NoteDay[] = await res.json();
      if (!Array.isArray(remoteDays)) {
        setSyncStatus('offline');
        syncFailCountRef.current++;
        notesPollFailCountRef.current++;
        if (syncFailCountRef.current === 2) {
          showToast('Notes sync offline — PC may be unreachable');
        }
        return;
      }

      // Per-bullet merge: iterate each remote day, merge individual bullets by Id.
      // Each bullet's LastEdited timestamp is compared independently — the most recent edit wins.
      // New bullets from either side are preserved. This prevents data loss on concurrent edits.
      setDays(prev => {
        const merged = [...prev];
        for (const remote of remoteDays) {
          const localIdx = merged.findIndex(d => d.Date === remote.Date);
          if (localIdx >= 0) {
            const localDay = merged[localIdx];
            // Merge bullets by Id: most-recently-edited wins per bullet
            const bulletMap = new Map<string, NoteBullet>();
            for (const lb of localDay.Bullets) bulletMap.set(lb.Id, lb);
            for (const rb of remote.Bullets) {
              const existing = bulletMap.get(rb.Id);
              if (!existing) {
                // New from remote — add it
                bulletMap.set(rb.Id, rb);
              } else {
                // Both have it — compare LastEdited timestamps
                const localTs = new Date(existing.LastEdited || existing.CreatedAt).getTime();
                const remoteTs = new Date(rb.LastEdited || rb.CreatedAt).getTime();
                if (remoteTs > localTs) {
                  bulletMap.set(rb.Id, rb); // Remote wins
                }
                // else: local wins, keep existing
              }
            }
            // Preserve order: remote order for remote bullets, append local-only bullets at end
            const remoteIds = new Set(remote.Bullets.map(b => b.Id));
            const orderedBullets: NoteBullet[] = [];
            // First: follow remote ordering for all bullets that exist in remote
            for (const rb of remote.Bullets) {
              const resolved = bulletMap.get(rb.Id);
              if (resolved) orderedBullets.push(resolved);
            }
            // Then: append local-only bullets (not in remote)
            for (const lb of localDay.Bullets) {
              if (!remoteIds.has(lb.Id)) orderedBullets.push(lb);
            }
            // Use the later LastModified for the day
            const dayTs = Math.max(localDay.LastModified || 0, remote.LastModified || 0);
            merged[localIdx] = { ...localDay, Bullets: orderedBullets, LastModified: dayTs };
          } else {
            merged.push(remote);
          }
        }
        // Persist after merge
        queueMicrotask(() => {
          if (isPersistingRef.current) return;
          isPersistingRef.current = true;
          AsyncStorage.setItem(`${NOTES_STORAGE_KEY}_pending`, JSON.stringify(merged))
            .then(() => AsyncStorage.setItem(NOTES_STORAGE_KEY, JSON.stringify(merged)))
            .then(() => AsyncStorage.removeItem(`${NOTES_STORAGE_KEY}_pending`))
            .catch(() => {})
            .finally(() => { isPersistingRef.current = false; });
        });
        return merged;
      });

      setSyncStatus('synced');
      syncFailCountRef.current = 0;
      notesPollFailCountRef.current = 0;

      // Clear dates that were merged from remote to avoid re-POSTing stale data
      // Only delete dates that were snapshotted before the fetch (race-condition fix)
      for (const remote of remoteDays) {
        if (dirtySnapshot.has(remote.Date)) modifiedDatesRef.current.delete(remote.Date);
      }

      // Flush offline queue: re-add pending dates and trigger POST
      try {
        const pendingRaw = await AsyncStorage.getItem(PENDING_NOTES_SYNC_KEY);
        if (pendingRaw) {
          const pendingDates: string[] = JSON.parse(pendingRaw);
          if (pendingDates.length > 0) {
            for (const dateKey of pendingDates) {
              modifiedDatesRef.current.add(dateKey);
            }
            await AsyncStorage.removeItem(PENDING_NOTES_SYNC_KEY);
            // Use queueMicrotask to defer — schedulePost may be defined after fetchRemoteNotes
            queueMicrotask(() => { if (schedulePostRef.current) schedulePostRef.current(); });
          }
        }
      } catch { /* ignore queue flush errors */ }
    } catch {
      setSyncStatus('offline');
      syncFailCountRef.current++;
      notesPollFailCountRef.current++;
      if (syncFailCountRef.current === 2) {
        showToast('Notes sync offline — PC may be unreachable');
      }
    }
  }, [pairingKey]);

  // Start / restart polling whenever fetchRemoteNotes changes
  useEffect(() => {
    // A-18 fix: skip polling if no pairing key (no paired device)
    if (!pairingKey) return;
    let active = true;
    const poll = async () => {
      await fetchRemoteNotes();
      if (!active) return;
      // Adaptive backoff: 10s on success, exponential up to 120s on consecutive failures
      const interval = notesPollFailCountRef.current === 0
        ? POLL_INTERVAL
        : Math.min(POLL_INTERVAL * Math.pow(2, notesPollFailCountRef.current), 120000);
      pollTimerRef.current = setTimeout(poll, interval);
    };
    poll();
    return () => {
      active = false;
      if (pollTimerRef.current) clearTimeout(pollTimerRef.current);
    };
  }, [fetchRemoteNotes, pairingKey]);

  // ═══════════════════════════════════════════════════════════
  // SYNC: Debounced POST of modified days (POST /api/notes)
  // ═══════════════════════════════════════════════════════════
  const schedulePost = useCallback(() => {
    if (debounceTimerRef.current) clearTimeout(debounceTimerRef.current);
    debounceTimerRef.current = setTimeout(async () => {
      if (!mountedRef.current) return;
      if (!pcUrlRef.current || !pairingKey) return;
      const modifiedDates = modifiedDatesRef.current;
      if (modifiedDates.size === 0) return;

      const currentDays = daysRef.current;
      const toPost = currentDays.filter(d => modifiedDates.has(d.Date));
      if (toPost.length === 0) return;

      try {
        setSyncStatus('syncing');
        const res = await fetchWithTimeout(`${pcUrlRef.current}/api/notes`, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            'X-FlyShelf-Client': 'MobileCompanion',
            'X-Pairing-Key': pairingKey,
            'X-Device-Name': deviceName || 'Android',
          },
          body: JSON.stringify(toPost),
        }, 5000);

        if (res.ok) {
          // M-7 Fix: Only remove the dates we successfully posted, not all modified dates
          for (const d of toPost) modifiedDatesRef.current.delete(d.Date);
          setSyncStatus('synced');
          syncFailCountRef.current = 0;
          // Clear pending offline queue on success
          AsyncStorage.removeItem(PENDING_NOTES_SYNC_KEY).catch(() => {});
        } else {
          setSyncStatus('offline');
          syncFailCountRef.current++;
          // A-16 FIX: Use shared helper instead of duplicated inline persist
          queueFailedDates(toPost.map(d => d.Date));
          if (syncFailCountRef.current === 2) {
            showToast('Notes sync offline — PC may be unreachable');
          }
        }
      } catch {
        setSyncStatus('offline');
        syncFailCountRef.current++;
        // A-16 FIX: Use shared helper instead of duplicated inline persist
        queueFailedDates(toPost.map(d => d.Date));
        if (syncFailCountRef.current === 2) {
          showToast('Notes sync offline — PC may be unreachable');
        }
      }
    }, DEBOUNCE_POST_MS);
  }, [pairingKey, deviceName]);

  // Keep ref in sync for offline queue flush inside fetchRemoteNotes
  useEffect(() => { schedulePostRef.current = schedulePost; }, [schedulePost]);

  return {
    days,
    setDays,
    syncStatus,
    isLoading,
    /** Add a date key here before calling schedulePost() to mark it dirty. */
    modifiedDatesRef,
    schedulePost,
    /** Stable ref to schedulePost — safe to capture in callbacks. */
    schedulePostRef,
    /** Stable ref to days — safe to read in callbacks without stale-closure risk. */
    daysRef,
    /** AsyncStorage key, exported so callers can persist edits via the same key. */
    NOTES_STORAGE_KEY,
  };
}
