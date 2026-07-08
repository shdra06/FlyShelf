/**
 * useTodoTimers.ts
 *
 * Extracted from app/(tabs)/todo.tsx — handles:
 *  - Countdown timer state (activeTimers map)
 *  - Starting a timer for a task (persists end-time to AsyncStorage)
 *  - Cancelling a timer
 *  - Restoring timers that were active before the app was killed / backgrounded
 *  - Firing haptic + Alert + local notification on timer completion
 *  - Leak-safe cleanup on unmount (T-3 fix: uses a ref, not setState)
 *
 * The hook is completely self-contained; the component only needs to pass in a
 * stable daysRef so the hook can look up task text for alert messages.
 */

import { useState, useCallback, useEffect, useRef } from 'react';
import { Alert } from 'react-native';
import AsyncStorage from '@react-native-async-storage/async-storage';
import * as Haptics from 'expo-haptics';
import * as Notifications from 'expo-notifications';

const TIMERS_STORAGE_KEY = '@flyshelf_active_timers';

// -- Public types -------------------------------------------------------------

/** Map of taskId -> { remaining seconds, intervalId } */
export type ActiveTimersMap = Record<string, { remaining: number; intervalId: ReturnType<typeof setInterval> }>;

export interface UseTodoTimersParams {
  /**
   * Ref pointing to the current days array so the hook can look up task text
   * for notification / alert bodies without capturing stale state.
   */
  daysRef: React.MutableRefObject<Array<{ Items: Array<{ Id: string; Text: string }> }>>;
  /**
   * The currently-selected day key, used when persisting a new timer.
   */
  selectedDayKey: string;
}

export interface UseTodoTimersReturn {
  /** Live countdown state — read this to render timers in the UI */
  activeTimers: ActiveTimersMap;
  /**
   * Ref that mirrors activeTimers — kept in sync via useEffect.
   * Exposed so the component can use it in its own unmount cleanup without
   * re-introducing the T-3 bug.
   */
  activeTimersRef: React.MutableRefObject<ActiveTimersMap>;
  /** Start a countdown timer for taskId (minutes must be > 0) */
  startTimer: (itemId: string, minutes: number) => void;
  /** Cancel an active timer and remove it from AsyncStorage */
  cancelTimer: (itemId: string) => void;
  /** Format a remaining-seconds count as "MM:SS" */
  formatCountdown: (totalSeconds: number) => string;
  /** Look up a task's display text by id (uses daysRef for stable access) */
  getItemText: (itemId: string) => string;
}

// -- Hook ---------------------------------------------------------------------

export function useTodoTimers({ daysRef, selectedDayKey }: UseTodoTimersParams): UseTodoTimersReturn {
  const [activeTimers, setActiveTimers] = useState<ActiveTimersMap>({});
  // T-3 fix: mirror active timers in a ref so unmount cleanup can clear the
  // interval handles directly. React does not run setState updaters on
  // unmounted components, so cleanup must never rely on them.
  const activeTimersRef = useRef<ActiveTimersMap>({});

  // Keep ref in sync with state
  useEffect(() => { activeTimersRef.current = activeTimers; }, [activeTimers]);

  // Helper: look up task text by id (uses ref to avoid stale closures)
  const getItemText = useCallback((itemId: string): string => {
    if (!daysRef.current || daysRef.current.length === 0) return 'Task';
    for (const d of daysRef.current) {
      const it = d.Items.find(i => i.Id === itemId);
      if (it) return it.Text || 'Task';
    }
    return 'Task';
  }, [daysRef]);

  // ─── Restore persisted timers on mount & clean up on unmount ─────────────
  useEffect(() => {
    const restoreTimers = async () => {
      try {
        const raw = await AsyncStorage.getItem(TIMERS_STORAGE_KEY);
        if (!raw) return;
        const saved: Record<string, { taskId: string; endTime: number; duration: number; dayKey: string }> = JSON.parse(raw);
        const now = Date.now();
        const toRemove: string[] = [];

        for (const [taskId, data] of Object.entries(saved)) {
          // Check the task still exists
          const taskExists = daysRef.current.some(d => d.Items.some(i => i.Id === taskId));
          if (!taskExists) { toRemove.push(taskId); continue; }

          const remainingMs = data.endTime - now;
          if (remainingMs <= 0) {
            // Timer expired while away — fire completion immediately
            toRemove.push(taskId);
            Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
            Alert.alert('⏱ Timer Complete!', `Your timer for "${getItemText(taskId)}" has finished.`);
            try {
              Notifications.scheduleNotificationAsync({
                content: { title: '⏱ Timer Complete', body: getItemText(taskId) },
                trigger: null,
              });
            } catch {}
          } else {
            // Resume countdown with remaining time
            const remainingSec = Math.ceil(remainingMs / 1000);
            const intervalId = setInterval(() => {
              setActiveTimers(prev => {
                const timer = prev[taskId];
                if (!timer || timer.remaining <= 1) {
                  clearInterval(intervalId);
                  Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
                  Alert.alert('⏱ Timer Complete!', `Your timer for "${getItemText(taskId)}" has finished.`);
                  try {
                    Notifications.scheduleNotificationAsync({
                      content: { title: '⏱ Timer Complete', body: getItemText(taskId) },
                      trigger: null,
                    });
                  } catch {}
                  // Remove from AsyncStorage on completion
                  AsyncStorage.getItem(TIMERS_STORAGE_KEY).then(r => {
                    if (r) {
                      const timers = JSON.parse(r);
                      delete timers[taskId];
                      AsyncStorage.setItem(TIMERS_STORAGE_KEY, JSON.stringify(timers)).catch(() => {});
                    }
                  }).catch(() => {});
                  const { [taskId]: _, ...rest } = prev;
                  return rest;
                }
                return { ...prev, [taskId]: { ...timer, remaining: timer.remaining - 1 } };
              });
            }, 1000);
            setActiveTimers(prev => ({ ...prev, [taskId]: { remaining: remainingSec, intervalId } }));
          }
        }

        // Clean up expired/orphaned timers from storage
        if (toRemove.length > 0) {
          for (const id of toRemove) delete saved[id];
          await AsyncStorage.setItem(TIMERS_STORAGE_KEY, JSON.stringify(saved));
        }
      } catch {}
    };
    restoreTimers();

    return () => {
      // T-3 fix: clear interval handles from the ref (AsyncStorage data is kept
      // for restoration). The previous code cleared them inside a setActiveTimers
      // updater, but React does not invoke state updaters on unmounted
      // components - the intervals leaked and kept firing forever.
      Object.values(activeTimersRef.current).forEach(t => clearInterval(t.intervalId));
      activeTimersRef.current = {};
    };
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  // ─── Start a timer ────────────────────────────────────────────────────────
  const startTimer = useCallback((itemId: string, minutes: number) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    const remaining = minutes * 60;
    const endTime = Date.now() + remaining * 1000;

    // Persist timer to AsyncStorage
    // AC-9: Wrap JSON.parse in try-catch to prevent crash on corrupt storage
    AsyncStorage.getItem(TIMERS_STORAGE_KEY).then(raw => {
      let timers: Record<string, any> = {};
      try { timers = raw ? JSON.parse(raw) : {}; } catch { timers = {}; }
      timers[itemId] = { taskId: itemId, endTime, duration: minutes * 60, dayKey: selectedDayKey };
      AsyncStorage.setItem(TIMERS_STORAGE_KEY, JSON.stringify(timers)).catch(() => {});
    }).catch(() => {});

    const intervalId = setInterval(() => {
      setActiveTimers(prev => {
        const timer = prev[itemId];
        if (!timer || timer.remaining <= 1) {
          clearInterval(intervalId);
          // Timer complete
          Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
          Alert.alert('⏱ Timer Complete!', `Your timer for "${getItemText(itemId)}" has finished.`);
          try {
            Notifications.scheduleNotificationAsync({
              content: { title: '⏱ Timer Complete', body: getItemText(itemId) },
              trigger: null,
            });
          } catch {}
          // Remove from AsyncStorage on completion
          AsyncStorage.getItem(TIMERS_STORAGE_KEY).then(r => {
            if (r) {
              const timers = JSON.parse(r);
              delete timers[itemId];
              AsyncStorage.setItem(TIMERS_STORAGE_KEY, JSON.stringify(timers)).catch(() => {});
            }
          }).catch(() => {});
          const { [itemId]: _, ...rest } = prev;
          return rest;
        }
        return { ...prev, [itemId]: { ...timer, remaining: timer.remaining - 1 } };
      });
    }, 1000);
    setActiveTimers(prev => ({ ...prev, [itemId]: { remaining, intervalId } }));
  }, [getItemText, selectedDayKey]);

  // ─── Cancel a timer ───────────────────────────────────────────────────────
  const cancelTimer = useCallback((itemId: string) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    setActiveTimers(prev => {
      const timer = prev[itemId];
      if (timer) clearInterval(timer.intervalId);
      const { [itemId]: _, ...rest } = prev;
      return rest;
    });
    // Remove from AsyncStorage
    AsyncStorage.getItem(TIMERS_STORAGE_KEY).then(raw => {
      if (raw) {
        const timers = JSON.parse(raw);
        delete timers[itemId];
        AsyncStorage.setItem(TIMERS_STORAGE_KEY, JSON.stringify(timers)).catch(() => {});
      }
    }).catch(() => {});
  }, []);

  // ─── Format countdown ─────────────────────────────────────────────────────
  const formatCountdown = (totalSeconds: number): string => {
    const m = Math.floor(totalSeconds / 60);
    const sec = totalSeconds % 60;
    return `${m.toString().padStart(2, '0')}:${sec.toString().padStart(2, '0')}`;
  };

  return { activeTimers, activeTimersRef, startTimer, cancelTimer, formatCountdown, getItemText };
}
