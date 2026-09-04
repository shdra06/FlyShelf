/**
 * useTodoSync.ts
 *
 * Extracted from app/(tabs)/todo.tsx — handles:
 *  - Resolving the PC URL (from SecureStorage / AsyncStorage)
 *  - Polling the PC for todos via HTTP every POLL_INTERVAL ms
 *  - Pushing changed days back to the PC (debounced)
 *  - Offline queue flush on reconnect
 *  - Adaptive fail-count tracking with a toast on the 2nd consecutive failure
 *
 * The hook owns its own polling interval and debounce timer and cleans them
 * up automatically on unmount.
 */

import { useCallback, useEffect, useRef } from 'react';
import { Platform } from 'react-native';
import AsyncStorage from '@react-native-async-storage/async-storage';
import EncryptedStorage from '../../utils/EncryptedStorage';

import { getSecureItem } from '../../utils/secureStorage';
import { fetchWithTimeout, resolveLivePcUrl } from '../../utils/networkHelpers';
import { TodoDay } from '../../utils/noteTypes';

// -- Shared constants (kept identical to todo.tsx) ----------------------------
const POLL_INTERVAL = 10_000;
const DEBOUNCE_POST_MS = 2_000;

// -- Public types -------------------------------------------------------------

export type SyncStatus = 'idle' | 'syncing' | 'connected' | 'offline';

export interface UseTodoSyncParams {
  pairingKey: string | null | undefined;
  deviceName: string | null | undefined;
  daysRef: React.MutableRefObject<TodoDay[]>;
  changedDayKeysRef: React.MutableRefObject<Set<string>>;
  mountedRef: React.MutableRefObject<boolean>;
  mergeDays: (local: TodoDay[], remote: TodoDay[]) => TodoDay[];
  saveLocal: (allDays: TodoDay[]) => Promise<void>;
  onDaysMerged: (merged: TodoDay[]) => void;
  onStatusChange: (status: SyncStatus) => void;
}

export interface UseTodoSyncReturn {
  schedulePush: (dayKey: string) => void;
}

// -- Hook ---------------------------------------------------------------------

export function useTodoSync({
  pairingKey,
  deviceName,
  daysRef,
  changedDayKeysRef,
  mountedRef,
  mergeDays,
  saveLocal,
  onDaysMerged,
  onStatusChange,
}: UseTodoSyncParams): UseTodoSyncReturn {
  const pcUrlRef = useRef<string>('');
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const pollCountRef = useRef(0);
  const syncFailCountRef = useRef(0);
  const todoPollFailCountRef = useRef(0);

  // ── Stable refs for callbacks to avoid infinite re-render loops ──
  const mergeDaysRef = useRef(mergeDays);
  useEffect(() => { mergeDaysRef.current = mergeDays; }, [mergeDays]);
  const saveLocalRef = useRef(saveLocal);
  useEffect(() => { saveLocalRef.current = saveLocal; }, [saveLocal]);
  const onDaysMergedRef = useRef(onDaysMerged);
  useEffect(() => { onDaysMergedRef.current = onDaysMerged; }, [onDaysMerged]);
  const onStatusChangeRef = useRef(onStatusChange);
  useEffect(() => { onStatusChangeRef.current = onStatusChange; }, [onStatusChange]);

  // Resolve PC URL
  const resolvePcUrl = useCallback(async () => {
    try {
      const live = await resolveLivePcUrl();
      if (live) { pcUrlRef.current = live; return; }
      const globalUrl = await getSecureItem('pairedGlobalUrl');
      if (globalUrl) { pcUrlRef.current = globalUrl; return; }
      const localIp = await AsyncStorage.getItem('@pcLocalIp');
      if (localIp) {
        let base = localIp.includes('://') ? localIp : `http://${localIp}`;
        const hostPart = base.replace(/^https?:\/\//, '');
        if (!hostPart.includes(':')) base = `${base}:8999`;
        pcUrlRef.current = base.replace(/\/$/, '');
      }
    } catch {}
  }, []);

  // Push changed days to PC
  const pushChangedDays = useCallback(async () => {
    if (!mountedRef.current) return;
    if (!pcUrlRef.current || !pairingKey) { onStatusChangeRef.current('offline'); return; }
    const keysToSync = new Set(changedDayKeysRef.current);
    const keys = Array.from(keysToSync);
    if (keys.length === 0) return;

    const payload = daysRef.current.filter(d => keys.includes(d.Date));
    if (payload.length === 0) return;

    try {
      onStatusChangeRef.current('syncing');
      const res = await fetchWithTimeout(
        `${pcUrlRef.current}/api/todos`,
        {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            'X-Pairing-Key': pairingKey,
            'X-Device-Name': deviceName || 'Android',
          },
          body: JSON.stringify(payload),
        },
        5000,
      );
      if (res.ok) {
        // Only clear the keys we successfully synced
        for (const k of keysToSync) changedDayKeysRef.current.delete(k);
        onStatusChangeRef.current('connected');
        syncFailCountRef.current = 0;
      } else {
        // Re-add failed keys for retry
        for (const k of keysToSync) changedDayKeysRef.current.add(k);
        onStatusChangeRef.current('offline');
      }
    } catch {
      // Re-add failed keys
      for (const k of keysToSync) changedDayKeysRef.current.add(k);
      // Persist failed sync keys for retry on next successful connection
      try {
        const existing = await EncryptedStorage.getItem('@flyshelf_pending_todo_sync') || await AsyncStorage.getItem('@flyshelf_pending_todo_sync');
        const pending: string[] = existing ? JSON.parse(existing) : [];
        for (const k of keys) { if (!pending.includes(k)) pending.push(k); }
        await EncryptedStorage.setItem('@flyshelf_pending_todo_sync', JSON.stringify(pending));
      } catch {}
    }
  }, [pairingKey, deviceName, daysRef, changedDayKeysRef, mountedRef]);

  // Debounced push (public)
  const schedulePush = useCallback((dayKey: string) => {
    changedDayKeysRef.current.add(dayKey);
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(pushChangedDays, DEBOUNCE_POST_MS);
  }, [pushChangedDays, changedDayKeysRef]);

  // Poll PC for todos
  const pollTodos = useCallback(async () => {
    // Re-resolve PC URL every 5th poll to pick up IP changes
    pollCountRef.current++;
    if (pollCountRef.current % 5 === 0 || !pcUrlRef.current) {
      await resolvePcUrl();
    }

    if (!pcUrlRef.current || !pairingKey) { onStatusChangeRef.current('offline'); return; }
    try {
      const resp = await fetchWithTimeout(
        `${pcUrlRef.current}/api/todos?days=15`,
        {
          method: 'GET',
          headers: {
            'X-Pairing-Key': pairingKey,
            'X-Device-Name': deviceName || 'Android',
          },
        },
        5000,
      );
      if (resp.ok) {
        const remote: TodoDay[] = await resp.json();
        // Normalize PC date keys: "2026-09-03T00:00:00.0000000+05:30" → "2026-09-03T00:00:00"
        for (const rd of remote) {
          if (rd.Date && rd.Date.includes('+')) {
            rd.Date = rd.Date.split('T')[0] + 'T00:00:00';
          }
        }
        const merged = mergeDaysRef.current(daysRef.current, remote);
        daysRef.current = merged;
        onDaysMergedRef.current(merged);
        saveLocalRef.current(merged);
        onStatusChangeRef.current('connected');
        syncFailCountRef.current = 0;
        todoPollFailCountRef.current = 0;

        // Flush offline queue on successful connection
        try {
          const pendingRaw = await EncryptedStorage.getItem('@flyshelf_pending_todo_sync') || await AsyncStorage.getItem('@flyshelf_pending_todo_sync');
          if (pendingRaw) {
            const pendingKeys: string[] = JSON.parse(pendingRaw);
            if (pendingKeys.length > 0) {
              const payload = daysRef.current.filter(d => pendingKeys.includes(d.Date));
              if (payload.length > 0) {
                await fetchWithTimeout(`${pcUrlRef.current}/api/todos`, {
                  method: 'POST',
                  headers: { 'Content-Type': 'application/json', 'X-Pairing-Key': pairingKey, 'X-Device-Name': deviceName || 'Android' },
                  body: JSON.stringify(payload),
                }, 5000);
              }
              await EncryptedStorage.removeItem('@flyshelf_pending_todo_sync');
              await AsyncStorage.removeItem('@flyshelf_pending_todo_sync').catch(() => {});
            }
          }
        } catch {}
      } else {
        syncFailCountRef.current++;
        todoPollFailCountRef.current++;
        if (syncFailCountRef.current >= 3) {
          onStatusChangeRef.current('offline');
        }
      }
    } catch {
      syncFailCountRef.current++;
      todoPollFailCountRef.current++;
      if (syncFailCountRef.current >= 3) {
        onStatusChangeRef.current('offline');
      }
    }
  }, [pairingKey, deviceName, resolvePcUrl, daysRef]);

  // Lifecycle: init polling with adaptive backoff, clean up on unmount
  useEffect(() => {
    // A-18 fix: skip polling if no pairing key (no paired device)
    if (!pairingKey) return;
    let active = true;
    const poll = async () => {
      await pollTodos();
      if (!active) return;
      // Adaptive backoff: 10s on success, exponential up to 120s on consecutive failures
      const interval = todoPollFailCountRef.current === 0
        ? POLL_INTERVAL
        : Math.min(POLL_INTERVAL * Math.pow(2, todoPollFailCountRef.current), 120000);
      pollRef.current = setTimeout(poll, interval);
    };
    resolvePcUrl().then(() => poll());
    return () => {
      active = false;
      if (pollRef.current) clearTimeout(pollRef.current);
      if (debounceRef.current) clearTimeout(debounceRef.current);
    };
  }, [pairingKey, resolvePcUrl, pollTodos]);

  return { schedulePush };
}
