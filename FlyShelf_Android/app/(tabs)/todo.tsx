import React, { useState, useEffect, useRef, useCallback, useMemo } from 'react';
import AppErrorBoundary from '../../components/AppErrorBoundary';
import {
  View, Text, TextInput, TouchableOpacity, ScrollView,
  Alert, Platform, Modal, Animated, KeyboardAvoidingView,
  ToastAndroid, FlatList,
} from 'react-native';
import { toast } from '../../context/ToastContext';

import { FlashList } from '@shopify/flash-list';
const FlashListCast = FlashList as any;
import { LinearGradient } from 'expo-linear-gradient';
import * as Haptics from 'expo-haptics';
import * as Notifications from 'expo-notifications';
import AsyncStorage from '@react-native-async-storage/async-storage';
import EncryptedStorage from '../../utils/EncryptedStorage';
import DateTimePicker from '@react-native-community/datetimepicker';

import { useSettings } from '../../context/SettingsContext';
import { NetworkClock } from '../../utils/networkClock';
import { useTodoSync } from '../../features/todo/useTodoSync';
import { useTodoTimers } from '../../features/todo/useTodoTimers';
import {
  TodoDay, TodoItem, TodoPriority, TodoRecurrence,
  createTodoItem, createTodoDay, generateId,
  PriorityLabels, PriorityColors, RecurrenceLabels,
  getDueDateDisplay, isOverdue, isToday, parseDate,
} from '../../utils/noteTypes';
import { createTodoStyles } from '../../styles/todoStyles';
import { useAppTheme } from '../../hooks/useAppTheme';
import { font, space } from '../../styles/theme';
import { fuzzyIsMatch } from '../../utils/textNormalize';
import { Ionicons } from '@expo/vector-icons';
import RAnimated, { useSharedValue, useAnimatedScrollHandler } from 'react-native-reanimated';
import ScreenHeader from '../../components/ScreenHeader';

// ═══════════════════════════════════════════════════════════
// CONSTANTS
// ═══════════════════════════════════════════════════════════

const TODOS_STORAGE_KEY = '@flyshelf_todos';
const MIGRATE_DATE_KEY = '@flyshelf_last_migrate_date';

const COLOR_OPTIONS = ['', '#6384FF', '#34D399', '#FBBF24', '#F87171', '#A78BFA', '#F472B6', '#38BDF8'];

// ═══════════════════════════════════════════════════════════
// TEMPLATES
// ═══════════════════════════════════════════════════════════

const TODO_TEMPLATES = [
  { name: '🛒 Grocery Shopping', items: ['Buy fruits and vegetables', 'Get milk and eggs', 'Pick up bread', 'Grab snacks', 'Check household supplies'] },
  { name: '🏠 Weekly Chores', items: ['Vacuum and mop floors', 'Do laundry', 'Clean bathroom', 'Take out trash', 'Organize desk'] },
  { name: '🧑‍💻 Work Standup', items: ['Review yesterday tasks', 'Plan today priorities', 'Check email and messages', 'Update project board'] },
  { name: '✈️ Travel Packing', items: ['Pack passport and tickets', 'Charge electronics', 'Pack toiletries', 'Check weather forecast', 'Set out-of-office'] },
];

// ═══════════════════════════════════════════════════════════
// SORT MODES
// ═══════════════════════════════════════════════════════════

type TodoSortMode = 'manual' | 'priority' | 'dueDate' | 'alphabetical' | 'createdAt';

const SORT_OPTIONS: { mode: TodoSortMode; label: string; icon: string }[] = [
  { mode: 'manual', label: 'Manual', icon: '✋' },
  { mode: 'priority', label: 'Priority', icon: '🔥' },
  { mode: 'dueDate', label: 'Due Date', icon: '📅' },
  { mode: 'alphabetical', label: 'Alphabetical', icon: '🔤' },
  { mode: 'createdAt', label: 'Created', icon: '🕐' },
];
const TIMER_PRESETS = [null, 5, 10, 15, 25, 30, 60] as const;

// ═══════════════════════════════════════════════════════════
// HELPERS
// ═══════════════════════════════════════════════════════════

/** Build 7-day date range centered on today */
const buildDayRange = (): Date[] => {
  const days: Date[] = [];
  const today = new Date();
  for (let i = -3; i <= 3; i++) {
    const d = new Date(today);
    d.setDate(today.getDate() + i);
    d.setHours(0, 0, 0, 0);
    days.push(d);
  }
  return days;
};

const formatDayKey = (date: Date): string =>
  date.toISOString().split('T')[0] + 'T00:00:00';

const shortDayName = (date: Date): string =>
  date.toLocaleDateString('en-US', { weekday: 'short' }).toUpperCase();

const isSameDay = (a: Date, b: Date) =>
  a.getFullYear() === b.getFullYear() &&
  a.getMonth() === b.getMonth() &&
  a.getDate() === b.getDate();

/** Check if date is overdue (before today) */
const isDueDateOverdue = (dueDateStr: string | null | undefined): boolean => {
  if (!dueDateStr) return false;
  const d = new Date(dueDateStr);
  d.setHours(0, 0, 0, 0);
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  return d < today;
};

/** Check if date is today */
const isDueDateToday = (dueDateStr: string | null | undefined): boolean => {
  if (!dueDateStr) return false;
  const d = new Date(dueDateStr);
  d.setHours(0, 0, 0, 0);
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  return d.getTime() === today.getTime();
};

// ═══════════════════════════════════════════════════════════
// MAIN SCREEN
// ═══════════════════════════════════════════════════════════

function TodoScreenInner() {
  const { pairingKey, deviceName } = useSettings();
  const { colors, shadows } = useAppTheme();
  const s = createTodoStyles(colors, shadows);

  // ─── State ─────────────────────────────────────────────
  const [days, setDays] = useState<TodoDay[]>([]);
  const [selectedDayIndex, setSelectedDayIndex] = useState(3); // default = today (center of 7)
  const [newTodoText, setNewTodoText] = useState('');
  const [expandedItemId, setExpandedItemId] = useState<string | null>(null);
  const [editingItemId, setEditingItemId] = useState<string | null>(null);
  const [editingSubtaskId, setEditingSubtaskId] = useState<string | null>(null);
  const [showDatePicker, setShowDatePicker] = useState(false);
  const [datePickerItemId, setDatePickerItemId] = useState<string | null>(null);
  const [datePickerValue, setDatePickerValue] = useState(new Date());
  const [showColorPicker, setShowColorPicker] = useState<string | null>(null);
  const [tagInputItemId, setTagInputItemId] = useState<string | null>(null);
  const [tagInputText, setTagInputText] = useState('');
  const [syncStatus, setSyncStatus] = useState<'idle' | 'syncing' | 'connected' | 'offline'>('idle');

  // ─── Search state ─────────────────────────────────────
  const [searchQuery, setSearchQuery] = useState('');
  const [isSearchActive, setIsSearchActive] = useState(false);

  // ─── Template modal state ─────────────────────────────
  const [showTemplateModal, setShowTemplateModal] = useState(false);

  // ─── Sort state ───────────────────────────────────────
  const [sortMode, setSortMode] = useState<TodoSortMode>('manual');
  const [showSortModal, setShowSortModal] = useState(false);
  const [showReminderPicker, setShowReminderPicker] = useState(false);
  const [reminderPickerItemId, setReminderPickerItemId] = useState<string | null>(null);
  const [reminderPickerValue, setReminderPickerValue] = useState(new Date());
  const [reminderPickerMode, setReminderPickerMode] = useState<'date' | 'time'>('date');

  // ─── Refs ──────────────────────────────────────────────
  const daysRef = useRef<TodoDay[]>([]);
  const changedDayKeysRef = useRef<Set<string>>(new Set());
  const dayRange = useMemo(() => buildDayRange(), []);
  const checkboxScales = useRef<Map<string, Animated.Value>>(new Map());
  const mountedRef = useRef(true);

  // Keep daysRef in sync
  useEffect(() => { daysRef.current = days; }, [days]);

  // Unmount guard
  useEffect(() => {
    return () => { mountedRef.current = false; };
  }, []);

  // ─── Get or create day for a date key ──────────────────
  const getOrCreateDay = useCallback((dateKey: string, currentDays: TodoDay[]): [TodoDay, TodoDay[]] => {
    const existing = currentDays.find(d => d.Date === dateKey);
    if (existing) return [existing, currentDays];
    const newDay = createTodoDay(new Date(dateKey));
    const updated = [...currentDays, newDay];
    return [newDay, updated];
  }, []);

  // ─── Load from AsyncStorage + migrate incomplete tasks ─
  const loadLocal = useCallback(async () => {
    try {
      let raw = await EncryptedStorage.getItem(TODOS_STORAGE_KEY);
      if (!raw) {
        raw = await AsyncStorage.getItem(TODOS_STORAGE_KEY);
      }
      if (raw) {
        let parsed: TodoDay[] = JSON.parse(raw);

        // ── Migrate incomplete tasks from yesterday ──────
        const today = new Date();
        today.setHours(0, 0, 0, 0);
        const todayKey = formatDayKey(today);
        const todayStr = today.toISOString().split('T')[0];

        const lastMigrate = await AsyncStorage.getItem(MIGRATE_DATE_KEY);
        if (lastMigrate !== todayStr) {
          const yesterday = new Date(today);
          yesterday.setDate(today.getDate() - 1);
          const yesterdayKey = formatDayKey(yesterday);

          const yesterdayDay = parsed.find(d => d.Date === yesterdayKey);
          if (yesterdayDay) {
            const incomplete = yesterdayDay.Items.filter(
              i => !i.IsDone && i.Text.trim() !== ''
            );

            if (incomplete.length > 0) {
              let todayDay = parsed.find(d => d.Date === todayKey);
              if (!todayDay) {
                todayDay = createTodoDay(today);
                parsed = [...parsed, todayDay];
              }

              const existingTexts = new Set(
                todayDay.Items.map(i => i.Text.toLowerCase())
              );
              let migratedCount = 0;

              for (const item of incomplete) {
                if (!existingTexts.has(item.Text.toLowerCase())) {
                  const migrated = createTodoItem(item.Text);
                  migrated.Priority = item.Priority;
                  migrated.DueDate = item.DueDate;
                  migrated.Tags = [...item.Tags];
                  migrated.Color = item.Color;
                  migrated.Description = item.Description;
                  migrated.Recurrence = item.Recurrence;
                  todayDay.Items.push(migrated);
                  existingTexts.add(item.Text.toLowerCase());
                  migratedCount++;
                }
              }

              if (migratedCount > 0) {
                parsed = parsed.map(d =>
                  d.Date === todayKey ? { ...todayDay!, LastModified: NetworkClock.now() } : d
                );
                toast.info('Tasks Rolled Over', `Carried over ${migratedCount} pending task${migratedCount > 1 ? 's' : ''} from yesterday`);
              }
            }
          }
          await AsyncStorage.setItem(MIGRATE_DATE_KEY, todayStr);
        }
        // ── End migration ────────────────────────────────

        daysRef.current = parsed;
        setDays(parsed);
        await EncryptedStorage.setItem(TODOS_STORAGE_KEY, JSON.stringify(parsed));
      }
    } catch {}
  }, []);

  // ─── Save to AsyncStorage ──────────────────────────────
  const saveLocal = useCallback(async (allDays: TodoDay[]) => {
    try {
      daysRef.current = allDays;
      await EncryptedStorage.setItem(TODOS_STORAGE_KEY, JSON.stringify(allDays));
    } catch (e) {
      console.warn('Todo saveLocal: error', (e as any)?.message || e);
    }
  }, []);

  // ─── T-1 fix: per-item merge with deletion tombstones ─────
  // Items merge by Id using per-item LastEdited (newer edit wins). Deletions
  // are recorded as tombstones in day.DeletedItems so they propagate across
  // devices instead of resurrecting; an item edited AFTER its deletion is
  // revived (newer timestamp wins either way). The previous day-level
  // last-write-wins silently discarded concurrent edits to other tasks.
  const mergeDays = useCallback((local: TodoDay[], remote: TodoDay[]): TodoDay[] => {
    const ts = (iso?: string): number => {
      if (!iso) return 0;
      const t = new Date(iso).getTime();
      return isNaN(t) ? 0 : t;
    };
    const TOMBSTONE_TTL = 30 * 24 * 60 * 60 * 1000; // Purge tombstones after 30 days
    const map = new Map<string, TodoDay>();
    for (const d of local) map.set(d.Date, d);
    for (const rd of remote) {
      const existing = map.get(rd.Date);
      if (!existing) { map.set(rd.Date, rd); continue; }

      // 1. Union tombstones by Id (latest DeletedAt wins)
      const tombs = new Map<string, number>();
      for (const t of existing.DeletedItems || []) tombs.set(t.Id, Math.max(tombs.get(t.Id) || 0, t.DeletedAt));
      for (const t of rd.DeletedItems || []) tombs.set(t.Id, Math.max(tombs.get(t.Id) || 0, t.DeletedAt));

      // 2. Union items by Id (newer LastEdited wins)
      const items = new Map<string, TodoItem>();
      for (const li of existing.Items) items.set(li.Id, li);
      for (const ri of rd.Items) {
        const ex = items.get(ri.Id);
        if (!ex || ts(ri.LastEdited) > ts(ex.LastEdited)) items.set(ri.Id, ri);
      }

      // 3. Apply tombstones: deletion wins unless the item was edited after it
      const now = NetworkClock.now();
      const mergedItems: TodoItem[] = [];
      for (const it of items.values()) {
        const deletedAt = tombs.get(it.Id) || 0;
        if (deletedAt > 0 && deletedAt >= ts(it.LastEdited)) continue; // stays deleted
        if (deletedAt > 0) tombs.delete(it.Id); // edited after deletion - revived
        mergedItems.push(it);
      }
      mergedItems.sort((a, b) => a.SortOrder - b.SortOrder);

      const mergedTombs = Array.from(tombs.entries())
        .filter(([, at]) => now - at < TOMBSTONE_TTL)
        .map(([Id, DeletedAt]) => ({ Id, DeletedAt }));

      map.set(rd.Date, {
        ...existing,
        Items: mergedItems,
        DeletedItems: mergedTombs,
        LastModified: Math.max(existing.LastModified || 0, rd.LastModified || 0),
      });
    }
    return Array.from(map.values()).sort((a, b) => a.Date.localeCompare(b.Date));
  }, []);

  // ─── useTodoSync hook (placed after saveLocal + mergeDays are declared) ─
  const { schedulePush } = useTodoSync({
    pairingKey,
    deviceName,
    daysRef,
    changedDayKeysRef,
    mountedRef,
    mergeDays,
    saveLocal,
    onDaysMerged: (merged) => {
      daysRef.current = merged;
      setDays(merged);
    },
    onStatusChange: (status) => setSyncStatus(status),
  });

  // ─── Mark day modified & trigger sync (M-8 fix: immutable copy) ──
  const markModified = useCallback((dayKey: string, inputDays: TodoDay[]) => {
    const updatedDays = inputDays.map(d =>
      d.Date === dayKey ? { ...d, LastModified: NetworkClock.now() } : d
    );
    daysRef.current = updatedDays;
    setDays(updatedDays);
    saveLocal(updatedDays);
    schedulePush(dayKey);
  }, [saveLocal, schedulePush]);

  // ─── Lifecycle: load local data on mount ──────────────
  useEffect(() => {
    loadLocal();
  }, [loadLocal]);

  // ─── Currently selected day key & items ────────────────
  const selectedDate = dayRange[selectedDayIndex];
  const selectedDayKey = formatDayKey(selectedDate);
  const selectedDay = days.find(d => d.Date === selectedDayKey);
  const rawTodoItems = selectedDay?.Items || [];

  // ─── useTodoTimers hook ────────────────────────────────
  const { activeTimers, activeTimersRef, startTimer, cancelTimer, formatCountdown, getItemText } = useTodoTimers({
    daysRef,
    selectedDayKey,
  });


  // ─── Sort items based on sort mode ─────────────────────
  const todoItems = useMemo(() => {
    const items = [...rawTodoItems];
    switch (sortMode) {
      case 'priority':
        items.sort((a, b) => {
          if (b.Priority !== a.Priority) return b.Priority - a.Priority;
          return a.SortOrder - b.SortOrder;
        });
        break;
      case 'dueDate':
        items.sort((a, b) => {
          if (!a.DueDate && !b.DueDate) return a.SortOrder - b.SortOrder;
          if (!a.DueDate) return 1;
          if (!b.DueDate) return -1;
          const cmp = new Date(a.DueDate).getTime() - new Date(b.DueDate).getTime();
          return cmp !== 0 ? cmp : a.SortOrder - b.SortOrder;
        });
        break;
      case 'alphabetical':
        items.sort((a, b) => a.Text.toLowerCase().localeCompare(b.Text.toLowerCase()));
        break;
      case 'createdAt':
        items.sort((a, b) => new Date(a.CreatedAt).getTime() - new Date(b.CreatedAt).getTime());
        break;
      case 'manual':
      default:
        items.sort((a, b) => a.SortOrder - b.SortOrder);
        break;
    }
    return items;
  }, [rawTodoItems, sortMode]);

  // ─── Summary counts ────────────────────────────────────
  const doneCount = todoItems.filter(i => i.IsDone).length;
  const totalCount = todoItems.length;

  // AL-8: Memoize extraData to prevent new array reference on every render
  const extraDataMemo = useMemo(
    () => [expandedItemId, editingItemId, showColorPicker, tagInputItemId, editingSubtaskId, activeTimers],
    [expandedItemId, editingItemId, showColorPicker, tagInputItemId, editingSubtaskId, activeTimers]
  );

  // ─── Search results (AUDIT FIX: fuzzy matching) ────────────────────────────────────
  const searchResults = useMemo(() => {
    if (!searchQuery.trim()) return [];
    const q = searchQuery.trim();
    const results: { day: TodoDay; item: TodoItem }[] = [];
    for (const day of days) {
      for (const item of day.Items) {
        const haystack = [item.Text, item.Description || '', ...item.Tags].join(' ');
        if (fuzzyIsMatch(q, haystack)) {
          results.push({ day, item });
        }
      }
    }
    return results;
  }, [searchQuery, days]);

  // ─── Checkbox animation helper ─────────────────────────
  const getCheckboxScale = useCallback((id: string) => {
    if (!checkboxScales.current.has(id)) {
      checkboxScales.current.set(id, new Animated.Value(1));
    }
    return checkboxScales.current.get(id)!;
  }, []);

  const animateCheckbox = useCallback((id: string) => {
    const scale = getCheckboxScale(id);
    Animated.sequence([
      Animated.timing(scale, { toValue: 0.7, duration: 80, useNativeDriver: true }),
      Animated.spring(scale, { toValue: 1, friction: 3, tension: 200, useNativeDriver: true }),
    ]).start();
  }, [getCheckboxScale]);

  // ═══════════════════════════════════════════════════════
  // HANDLERS
  // ═══════════════════════════════════════════════════════

  const handleAddTodo = useCallback(() => {
    const text = newTodoText.trim();
    if (!text) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);

    const item = createTodoItem(text);
    item.CreatedByDevice = deviceName || 'Android';
    item.LastEditedByDevice = deviceName || 'Android';
    const [day, withDay] = getOrCreateDay(selectedDayKey, daysRef.current);
    const updatedDay = { ...day, Items: [...day.Items, item] };
    const updated = withDay.map(d => d.Date === selectedDayKey ? updatedDay : d);
    // If getOrCreateDay added a new day, and it's not yet in the list
    if (!withDay.some(d => d.Date === selectedDayKey)) {
      updated.push(updatedDay);
    }
    markModified(selectedDayKey, updated);
    setNewTodoText('');
  }, [newTodoText, selectedDayKey, getOrCreateDay, markModified, deviceName]);

  const handleToggleDone = useCallback((itemId: string) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    animateCheckbox(itemId);

    // Find the task being toggled to check recurrence
    const currentDay = daysRef.current.find(d => d.Date === selectedDayKey);
    const toggledItem = currentDay?.Items.find(i => i.Id === itemId);
    const isBeingMarkedDone = toggledItem && !toggledItem.IsDone;

    let updated = daysRef.current.map(d => {
      if (d.Date !== selectedDayKey) return d;
      return {
        ...d,
        Items: d.Items.map(item =>
          item.Id === itemId ? { ...item, IsDone: !item.IsDone, LastEdited: new Date().toISOString() } : item
        ),
      };
    });
    markModified(selectedDayKey, updated);

    // ── Recurring task auto-creation ──────────────────────
    if (isBeingMarkedDone && toggledItem.Recurrence > 0) {
      const recurrence = toggledItem.Recurrence;
      // Calculate next due date from current due date (or from today if none)
      const baseDate = toggledItem.DueDate ? new Date(toggledItem.DueDate) : new Date();
      baseDate.setHours(0, 0, 0, 0);
      const nextDue = new Date(baseDate);
      if (recurrence === 1) {
        // Daily: +1 day
        nextDue.setDate(nextDue.getDate() + 1);
      } else if (recurrence === 2) {
        // Weekly: +7 days
        nextDue.setDate(nextDue.getDate() + 7);
      } else if (recurrence === 3) {
        // Monthly: +1 month
        nextDue.setMonth(nextDue.getMonth() + 1);
      }

      const newItem = createTodoItem(toggledItem.Text);
      newItem.Description = toggledItem.Description;
      newItem.Tags = [...toggledItem.Tags];
      newItem.Color = toggledItem.Color;
      newItem.Priority = toggledItem.Priority;
      newItem.Recurrence = toggledItem.Recurrence;
      newItem.DueDate = nextDue.toISOString();
      newItem.SortOrder = 0;
      newItem.ReminderAt = null;

      const nextDayKey = formatDayKey(nextDue);
      // Use the latest days state (after toggle was applied)
      const latestDays = daysRef.current;
      const [targetDay, withTargetDay] = getOrCreateDay(nextDayKey, latestDays);
      const updatedTargetDay = { ...targetDay, Items: [newItem, ...targetDay.Items] };
      let finalDays = withTargetDay.map(d => d.Date === nextDayKey ? updatedTargetDay : d);
      if (!withTargetDay.some(d => d.Date === nextDayKey)) {
        finalDays.push(updatedTargetDay);
      }
      markModified(nextDayKey, finalDays);

      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
      const label = recurrence === 1 ? 'tomorrow' : recurrence === 2 ? 'next week' : 'next month';
      toast.success('Recurring Task Created', `Next occurrence scheduled for ${label}`);
    }
  }, [selectedDayKey, markModified, animateCheckbox, getOrCreateDay]);

  const handleDeleteItem = useCallback((itemId: string) => {
    Alert.alert('Delete Todo', 'Are you sure you want to delete this item?', [
      { text: 'Cancel', style: 'cancel' },
      {
        text: 'Delete', style: 'destructive', onPress: () => {
          Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Heavy);
          // T-1 fix: record a deletion tombstone so the delete propagates to
          // paired devices instead of the item resurrecting on next sync.
          const deletedAt = NetworkClock.now();
          const updated = daysRef.current.map(d => {
            if (d.Date !== selectedDayKey) return d;
            return {
              ...d,
              Items: d.Items.filter(i => i.Id !== itemId),
              DeletedItems: [...(d.DeletedItems || []).filter(t => t.Id !== itemId), { Id: itemId, DeletedAt: deletedAt }],
            };
          });
          markModified(selectedDayKey, updated);
          if (expandedItemId === itemId) setExpandedItemId(null);
        },
      },
    ]);
  }, [selectedDayKey, markModified, expandedItemId]);

  const handleUpdateItem = useCallback((itemId: string, patch: Partial<TodoItem>) => {
    const updated = daysRef.current.map(d => {
      if (d.Date !== selectedDayKey) return d;
      return {
        ...d,
        Items: d.Items.map(item =>
          item.Id === itemId ? { ...item, ...patch, LastEdited: new Date().toISOString() } : item
        ),
      };
    });
    markModified(selectedDayKey, updated);
  }, [selectedDayKey, markModified]);

  const handleCyclePriority = useCallback((item: TodoItem) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const next = ((item.Priority + 1) % 4) as TodoPriority;
    handleUpdateItem(item.Id, { Priority: next });
  }, [handleUpdateItem]);

  const handleOpenDatePicker = useCallback((item: TodoItem) => {
    setDatePickerItemId(item.Id);
    setDatePickerValue(item.DueDate ? new Date(item.DueDate) : new Date());
    setShowDatePicker(true);
  }, []);

  const handleDateChange = useCallback((_event: any, date?: Date) => {
    if (Platform.OS === 'android') {
      setShowDatePicker(false);
      if (date && datePickerItemId) {
        handleUpdateItem(datePickerItemId, { DueDate: date.toISOString() });
      }
    } else if (date) {
      setDatePickerValue(date);
    }
  }, [datePickerItemId, handleUpdateItem]);

  const handleDatePickerDone = useCallback(() => {
    if (datePickerItemId) {
      handleUpdateItem(datePickerItemId, { DueDate: datePickerValue.toISOString() });
    }
    setShowDatePicker(false);
  }, [datePickerItemId, datePickerValue, handleUpdateItem]);

  const handleClearDueDate = useCallback(() => {
    if (datePickerItemId) {
      handleUpdateItem(datePickerItemId, { DueDate: null });
    }
    setShowDatePicker(false);
  }, [datePickerItemId, handleUpdateItem]);

  const handleSetColor = useCallback((itemId: string, color: string) => {
    handleUpdateItem(itemId, { Color: color });
    setShowColorPicker(null);
  }, [handleUpdateItem]);

  const handleAddTag = useCallback((itemId: string) => {
    const tag = tagInputText.trim();
    if (!tag) return;
    const item = todoItems.find(i => i.Id === itemId);
    if (!item || item.Tags.includes(tag)) return;
    handleUpdateItem(itemId, { Tags: [...item.Tags, tag] });
    setTagInputText('');
    setTagInputItemId(null);
  }, [tagInputText, todoItems, handleUpdateItem]);

  const handleRemoveTag = useCallback((itemId: string, tag: string) => {
    const item = todoItems.find(i => i.Id === itemId);
    if (!item) return;
    handleUpdateItem(itemId, { Tags: item.Tags.filter(t => t !== tag) });
  }, [todoItems, handleUpdateItem]);

  // ─── Subtasks ──────────────────────────────────────────
  const handleAddSubtask = useCallback((itemId: string) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const sub = createTodoItem('');
    const updated = daysRef.current.map(d => {
      if (d.Date !== selectedDayKey) return d;
      return {
        ...d,
        Items: d.Items.map(item =>
          item.Id === itemId
            ? { ...item, SubTasks: [...item.SubTasks, sub], LastEdited: new Date().toISOString() }
            : item
        ),
      };
    });
    markModified(selectedDayKey, updated);
    setEditingSubtaskId(sub.Id);
  }, [selectedDayKey, markModified]);

  const handleToggleSubtask = useCallback((itemId: string, subId: string) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const updated = daysRef.current.map(d => {
      if (d.Date !== selectedDayKey) return d;
      return {
        ...d,
        Items: d.Items.map(item =>
          item.Id === itemId
            ? {
                ...item,
                LastEdited: new Date().toISOString(),
                SubTasks: item.SubTasks.map(st =>
                  st.Id === subId ? { ...st, IsDone: !st.IsDone } : st
                ),
              }
            : item
        ),
      };
    });
    markModified(selectedDayKey, updated);
  }, [selectedDayKey, markModified]);

  const handleUpdateSubtaskText = useCallback((itemId: string, subId: string, text: string) => {
    const updated = daysRef.current.map(d => {
      if (d.Date !== selectedDayKey) return d;
      return {
        ...d,
        Items: d.Items.map(item =>
          item.Id === itemId
            ? {
                ...item,
                LastEdited: new Date().toISOString(),
                SubTasks: item.SubTasks.map(st =>
                  st.Id === subId ? { ...st, Text: text } : st
                ),
              }
            : item
        ),
      };
    });
    markModified(selectedDayKey, updated);
  }, [selectedDayKey, markModified]);

  const handleDeleteSubtask = useCallback((itemId: string, subId: string) => {
    const updated = daysRef.current.map(d => {
      if (d.Date !== selectedDayKey) return d;
      return {
        ...d,
        Items: d.Items.map(item =>
          item.Id === itemId
            ? { ...item, LastEdited: new Date().toISOString(), SubTasks: item.SubTasks.filter(st => st.Id !== subId) }
            : item
        ),
      };
    });
    markModified(selectedDayKey, updated);
  }, [selectedDayKey, markModified]);

  const handleCycleRecurrence = useCallback((item: TodoItem) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const next = ((item.Recurrence + 1) % 4) as TodoRecurrence;
    handleUpdateItem(item.Id, { Recurrence: next });
  }, [handleUpdateItem]);

  // ─── Template handler ─────────────────────────────────
  const handleApplyTemplate = useCallback((template: typeof TODO_TEMPLATES[0]) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    const newItems = template.items.map((text, idx) => {
      const item = createTodoItem(text);
      item.SortOrder = (rawTodoItems.length + idx);
      return item;
    });
    const [day, withDay] = getOrCreateDay(selectedDayKey, daysRef.current);
    const updatedDay = { ...day, Items: [...day.Items, ...newItems] };
    const updated = withDay.map(d => d.Date === selectedDayKey ? updatedDay : d);
    if (!withDay.some(d => d.Date === selectedDayKey)) {
      updated.push(updatedDay);
    }
    markModified(selectedDayKey, updated);
    setShowTemplateModal(false);
    toast.success('Template Applied', `Added ${newItems.length} tasks to ${selectedDayKey}`);
  }, [selectedDayKey, rawTodoItems, getOrCreateDay, markModified]);

  // ─── Sort handler ─────────────────────────────────────
  const handleSelectSort = useCallback((mode: TodoSortMode) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    setSortMode(mode);
    setShowSortModal(false);
  }, []);

  // ─── Navigate to search result day ────────────────────
  const handleSearchResultTap = useCallback((dayDate: string) => {
    const idx = dayRange.findIndex(d => formatDayKey(d) === dayDate);
    if (idx >= 0) {
      setSelectedDayIndex(idx);
      setSearchQuery('');
      setIsSearchActive(false);
    }
  }, [dayRange]);

  // ─── Timer helpers ─────────────────────────────────────
  const handleCycleTimer = useCallback((item: TodoItem) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const currentIdx = TIMER_PRESETS.indexOf(item.TimerMinutes as any);
    const nextIdx = (currentIdx + 1) % TIMER_PRESETS.length;
    handleUpdateItem(item.Id, { TimerMinutes: TIMER_PRESETS[nextIdx] });
  }, [handleUpdateItem]);

  // ─── Reminder helpers ──────────────────────────────────
  const handleOpenReminderPicker = useCallback((item: TodoItem) => {
    setReminderPickerItemId(item.Id);
    setReminderPickerValue(item.ReminderAt ? new Date(item.ReminderAt) : new Date());
    setReminderPickerMode('date');
    setShowReminderPicker(true);
  }, []);

  const handleReminderDateChange = useCallback((_event: any, date?: Date) => {
    if (Platform.OS === 'android') {
      if (!date) { setShowReminderPicker(false); return; }
      if (reminderPickerMode === 'date') {
        // After picking date on Android, switch to time picker
        setReminderPickerValue(date);
        setReminderPickerMode('time');
      } else {
        // Time picked — finalize
        setShowReminderPicker(false);
        if (reminderPickerItemId) {
          const finalDate = new Date(reminderPickerValue);
          finalDate.setHours(date.getHours(), date.getMinutes(), 0, 0);
          handleUpdateItem(reminderPickerItemId, { ReminderAt: finalDate.toISOString() });
          if (finalDate > new Date()) {
            try {
              Notifications.scheduleNotificationAsync({
                content: {
                  title: '🔔 Todo Reminder',
                  body: getItemText(reminderPickerItemId) || 'You have a pending task',
                },
                trigger: { type: Notifications.SchedulableTriggerInputTypes.DATE, date: finalDate },
              });
            } catch {}
          }
        }
      }
    } else if (date) {
      setReminderPickerValue(date);
    }
  }, [reminderPickerItemId, reminderPickerMode, reminderPickerValue, handleUpdateItem, getItemText]);

  const handleReminderPickerDone = useCallback(() => {
    if (reminderPickerItemId) {
      handleUpdateItem(reminderPickerItemId, { ReminderAt: reminderPickerValue.toISOString() });
      if (reminderPickerValue > new Date()) {
        try {
          Notifications.scheduleNotificationAsync({
            content: {
              title: '🔔 Todo Reminder',
              body: getItemText(reminderPickerItemId) || 'You have a pending task',
            },
            trigger: { type: Notifications.SchedulableTriggerInputTypes.DATE, date: reminderPickerValue },
          });
        } catch {}
      }
    }
    setShowReminderPicker(false);
  }, [reminderPickerItemId, reminderPickerValue, handleUpdateItem, getItemText]);

  const handleClearReminder = useCallback(() => {
    if (reminderPickerItemId) {
      handleUpdateItem(reminderPickerItemId, { ReminderAt: null });
    }
    setShowReminderPicker(false);
  }, [reminderPickerItemId, handleUpdateItem]);

  const formatReminderTime = (dateStr: string): string => {
    const d = new Date(dateStr);
    return d.toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit', hour12: true });
  };

  const formatReminderDate = (dateStr: string): string => {
    const d = new Date(dateStr);
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    const reminderDay = new Date(d);
    reminderDay.setHours(0, 0, 0, 0);
    if (reminderDay.getTime() === today.getTime()) {
      return formatReminderTime(dateStr);
    }
    return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' }) + ' ' + formatReminderTime(dateStr);
  };

  // ═══════════════════════════════════════════════════════
  // RENDER HELPERS
  // ═══════════════════════════════════════════════════════

  const renderDaySelector = () => (
    <ScrollView
      horizontal
      showsHorizontalScrollIndicator={false}
      contentContainerStyle={{ paddingRight: space.xl }}
      style={s.daySelectorContainer}
    >
      {dayRange.map((date, index) => {
        const isActive = index === selectedDayIndex;
        const isDateToday = isSameDay(date, new Date());
        return (
          <TouchableOpacity
            key={index}
            style={[
              s.dayChip,
              isDateToday && s.dayChipToday,
              isActive && s.dayChipActive,
            ]}
            onPress={() => {
              Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
              setSelectedDayIndex(index);
            }}
            activeOpacity={0.7}
            accessibilityLabel={`${isDateToday ? 'Today' : shortDayName(date)} ${date.getDate()}${isActive ? ', selected' : ''}`}
            accessibilityRole="tab"
          >
            <Text style={[s.dayChipDayName, isActive && s.dayChipDayNameActive]}>
              {isDateToday ? 'TODAY' : shortDayName(date)}
            </Text>
            <Text style={[s.dayChipDate, isActive && s.dayChipDateActive]}>
              {date.getDate()}
            </Text>
          </TouchableOpacity>
        );
      })}
    </ScrollView>
  );

  const renderPriorityBadge = (item: TodoItem) => {
    if (item.Priority === 0) return null;
    const color = PriorityColors[item.Priority];
    return (
      <TouchableOpacity
        style={[s.priorityBadge, { backgroundColor: color + '18' }]}
        onPress={() => handleCyclePriority(item)}
        activeOpacity={0.7}
        accessibilityLabel={`Priority: ${PriorityLabels[item.Priority]}. Tap to change`}
        accessibilityRole="button"
      >
        <View style={[s.priorityDot, { backgroundColor: color }]} />
        <Text style={[s.priorityText, { color }]}>{PriorityLabels[item.Priority]}</Text>
      </TouchableOpacity>
    );
  };

  const renderDueDateChip = (item: TodoItem) => {
    if (!item.DueDate) return null;
    const display = getDueDateDisplay(item.DueDate);
    const overdue = isDueDateOverdue(item.DueDate) && !item.IsDone;
    const today = isDueDateToday(item.DueDate);
    return (
      <TouchableOpacity
        style={[
          s.dueDateChip,
          overdue && s.dueDateChipOverdue,
          today && s.dueDateChipToday,
        ]}
        onPress={() => handleOpenDatePicker(item)}
        activeOpacity={0.7}
        accessibilityLabel={`Due date: ${display}${overdue ? ', overdue' : ''}`}
        accessibilityRole="button"
      >
        <Text style={{ fontSize: 11 }}>📅</Text>
        <Text style={[
          s.dueDateText,
          overdue && s.dueDateTextOverdue,
          today && s.dueDateTextToday,
        ]}>
          {display}
        </Text>
      </TouchableOpacity>
    );
  };

  const renderRecurrenceBadge = (item: TodoItem) => {
    if (item.Recurrence === 0) return null;
    return (
      <TouchableOpacity
        style={s.recurrenceBadge}
        onPress={() => handleCycleRecurrence(item)}
        activeOpacity={0.7}
        accessibilityLabel={`Recurrence: ${RecurrenceLabels[item.Recurrence]}. Tap to change`}
        accessibilityRole="button"
      >
        <Text style={s.recurrenceText}>{RecurrenceLabels[item.Recurrence]}</Text>
      </TouchableOpacity>
    );
  };

  const renderTags = (item: TodoItem) => {
    if (item.Tags.length === 0 && tagInputItemId !== item.Id) return null;
    return (
      <>
        {item.Tags.map((tag, i) => (
          <TouchableOpacity
            key={i}
            style={s.tagPill}
            onLongPress={() => handleRemoveTag(item.Id, tag)}
            onPress={() => setTagInputItemId(tagInputItemId === item.Id ? null : item.Id)}
            activeOpacity={0.7}
            accessibilityLabel={`Tag: ${tag}. Long press to remove`}
            accessibilityRole="button"
          >
            <Text style={s.tagPillText}>#{tag}</Text>
          </TouchableOpacity>
        ))}
      </>
    );
  };

  const renderSubtasks = (item: TodoItem) => {
    const doneSubs = item.SubTasks.filter(st => st.IsDone).length;
    const totalSubs = item.SubTasks.length;
    return (
      <View style={s.subtaskSection}>
        <View style={s.subtaskHeader}>
          <Text style={s.subtaskLabel}>Subtasks {totalSubs > 0 ? `(${doneSubs}/${totalSubs})` : ''}</Text>
        </View>
        {item.SubTasks.map(sub => (
          <View key={sub.Id} style={s.subtaskRow}>
            <TouchableOpacity
              style={[s.subtaskCheckbox, sub.IsDone && s.subtaskCheckboxDone]}
              onPress={() => handleToggleSubtask(item.Id, sub.Id)}
              activeOpacity={0.7}
              accessibilityLabel={`Subtask: ${sub.Text || 'Untitled'}, ${sub.IsDone ? 'completed' : 'not completed'}`}
              accessibilityRole="checkbox"
            >
              {sub.IsDone && <Text style={s.subtaskCheckmark}>✓</Text>}
            </TouchableOpacity>
            {editingSubtaskId === sub.Id ? (
              <TextInput
                style={s.subtaskTextInput}
                value={sub.Text}
                onChangeText={(t) => handleUpdateSubtaskText(item.Id, sub.Id, t)}
                onBlur={() => {
                  setEditingSubtaskId(null);
                  if (!sub.Text.trim()) handleDeleteSubtask(item.Id, sub.Id);
                }}
                autoFocus
                placeholder="Subtask..."
                placeholderTextColor={colors.text.tertiary}
              />
            ) : (
              <TouchableOpacity
                style={{ flex: 1 }}
                onPress={() => setEditingSubtaskId(sub.Id)}
                activeOpacity={0.7}
                accessibilityLabel={`Edit subtask: ${sub.Text || 'Untitled'}`}
                accessibilityRole="button"
              >
                <Text style={sub.IsDone ? s.subtaskTextDone : s.subtaskText}>
                  {sub.Text || 'Untitled'}
                </Text>
              </TouchableOpacity>
            )}
            <TouchableOpacity
              style={s.subtaskDeleteBtn}
              onPress={() => handleDeleteSubtask(item.Id, sub.Id)}
              accessibilityLabel={`Delete subtask: ${sub.Text || 'Untitled'}`}
              accessibilityRole="button"
              hitSlop={{ top: 8, bottom: 8, left: 8, right: 8 }}
            >
              <Text style={{ color: colors.text.tertiary, fontSize: 14 }}>✕</Text>
            </TouchableOpacity>
          </View>
        ))}
        {/* Progress bar */}
        {totalSubs > 0 && (
          <View style={s.progressBar}>
            <View style={[s.progressFill, { width: `${(doneSubs / totalSubs) * 100}%` }]} />
          </View>
        )}
        <TouchableOpacity
          style={s.addSubtaskBtn}
          onPress={() => handleAddSubtask(item.Id)}
          activeOpacity={0.7}
          accessibilityLabel="Add subtask"
          accessibilityRole="button"
        >
          <Text style={{ color: colors.accent.primary, fontSize: 14 }}>＋</Text>
          <Text style={s.addSubtaskText}>Add subtask</Text>
        </TouchableOpacity>
      </View>
    );
  };

  const renderExpandedArea = (item: TodoItem) => {
    if (expandedItemId !== item.Id) return null;
    return (
      <View style={s.expandedArea}>
        {/* Description */}
        <View style={s.descriptionArea}>
          <TextInput
            style={s.descriptionInput}
            multiline
            value={item.Description}
            onChangeText={(t) => handleUpdateItem(item.Id, { Description: t })}
            placeholder="Add description..."
            placeholderTextColor={colors.text.tertiary}
          />
        </View>

        {/* Subtasks */}
        {renderSubtasks(item)}

        {/* Tag input */}
        {tagInputItemId === item.Id && (
          <View style={s.tagInputContainer}>
            <TextInput
              style={s.tagInput}
              value={tagInputText}
              onChangeText={setTagInputText}
              placeholder="Add tag..."
              placeholderTextColor={colors.text.tertiary}
              onSubmitEditing={() => handleAddTag(item.Id)}
              autoFocus
            />
            <TouchableOpacity style={s.tagAddBtn} onPress={() => handleAddTag(item.Id)}>
              <Text style={s.tagAddBtnText}>Add</Text>
            </TouchableOpacity>
          </View>
        )}

        {/* Color picker */}
        {showColorPicker === item.Id && (
          <View style={s.colorDotsRow}>
            {COLOR_OPTIONS.map((c, i) => (
              <TouchableOpacity
                key={i}
                style={[
                  s.colorDot,
                  { backgroundColor: c || colors.bg.input },
                  item.Color === c && s.colorDotSelected,
                ]}
                onPress={() => handleSetColor(item.Id, c)}
              />
            ))}
          </View>
        )}

        {/* Action buttons row */}
        <View style={s.actionRow}>
          <TouchableOpacity
            style={s.actionBtn}
            onPress={() => handleCyclePriority(item)}
            activeOpacity={0.7}
            accessibilityLabel={`Set priority, currently ${item.Priority === 0 ? 'none' : PriorityLabels[item.Priority]}`}
            accessibilityRole="button"
          >
            <Text style={{ fontSize: 12 }}>🔥</Text>
            <Text style={s.actionBtnText}>
              {item.Priority === 0 ? 'Priority' : PriorityLabels[item.Priority]}
            </Text>
          </TouchableOpacity>

          <TouchableOpacity
            style={s.actionBtn}
            onPress={() => handleOpenDatePicker(item)}
            activeOpacity={0.7}
            accessibilityLabel="Set due date"
            accessibilityRole="button"
          >
            <Text style={{ fontSize: 12 }}>📅</Text>
            <Text style={s.actionBtnText}>Due date</Text>
          </TouchableOpacity>

          <TouchableOpacity
            style={s.actionBtn}
            onPress={() => handleCycleRecurrence(item)}
            activeOpacity={0.7}
            accessibilityLabel={`Set recurrence, currently ${item.Recurrence === 0 ? 'none' : RecurrenceLabels[item.Recurrence]}`}
            accessibilityRole="button"
          >
            <Text style={{ fontSize: 12 }}>🔄</Text>
            <Text style={s.actionBtnText}>
              {item.Recurrence === 0 ? 'Repeat' : RecurrenceLabels[item.Recurrence].replace('🔄 ', '')}
            </Text>
          </TouchableOpacity>

          <TouchableOpacity
            style={s.actionBtn}
            onPress={() => setShowColorPicker(showColorPicker === item.Id ? null : item.Id)}
            activeOpacity={0.7}
            accessibilityLabel="Set task color"
            accessibilityRole="button"
          >
            <View style={{ width: 12, height: 12, borderRadius: 6, backgroundColor: item.Color || colors.accent.primary }} />
            <Text style={s.actionBtnText}>Color</Text>
          </TouchableOpacity>

          <TouchableOpacity
            style={s.actionBtn}
            onPress={() => setTagInputItemId(tagInputItemId === item.Id ? null : item.Id)}
            activeOpacity={0.7}
            accessibilityLabel="Manage tags"
            accessibilityRole="button"
          >
            <Text style={{ fontSize: 12 }}>🏷</Text>
            <Text style={s.actionBtnText}>Tags</Text>
          </TouchableOpacity>

          <TouchableOpacity
            style={s.actionBtn}
            onPress={() => handleCycleTimer(item)}
            activeOpacity={0.7}
            accessibilityLabel={`Set timer${item.TimerMinutes ? `, ${item.TimerMinutes} minutes` : ''}`}
            accessibilityRole="button"
          >
            <Text style={{ fontSize: 12 }}>⏱</Text>
            <Text style={s.actionBtnText}>
              {item.TimerMinutes ? `${item.TimerMinutes}m` : 'Timer'}
            </Text>
          </TouchableOpacity>

          <TouchableOpacity
            style={s.actionBtn}
            onPress={() => handleOpenReminderPicker(item)}
            activeOpacity={0.7}
            accessibilityLabel={item.ReminderAt ? 'Change reminder' : 'Set reminder'}
            accessibilityRole="button"
          >
            <Text style={{ fontSize: 12 }}>🔔</Text>
            <Text style={s.actionBtnText}>
              {item.ReminderAt ? 'Change' : 'Remind'}
            </Text>
          </TouchableOpacity>

          <TouchableOpacity
            style={[s.actionBtn, s.actionBtnDanger]}
            onPress={() => handleDeleteItem(item.Id)}
            activeOpacity={0.7}
            accessibilityLabel="Delete task"
            accessibilityRole="button"
          >
            <Text style={{ fontSize: 12 }}>🗑</Text>
            <Text style={[s.actionBtnText, s.actionBtnDangerText]}>Delete</Text>
          </TouchableOpacity>
        </View>
      </View>
    );
  };

  // ─── Render single todo item ───────────────────────────
  const renderTodoItem = useCallback(({ item }: { item: TodoItem }) => {
    const isExpanded = expandedItemId === item.Id;
    const isEditing = editingItemId === item.Id;
    const overdue = isOverdue(item);
    const scale = getCheckboxScale(item.Id);

    return (
      <TouchableOpacity
        style={[
          s.todoCard,
          overdue && s.todoCardOverdue,
          item.IsDone && s.todoCardDone,
        ]}
        onLongPress={() => handleDeleteItem(item.Id)}
        activeOpacity={0.85}
        onPress={() => {
          if (!isEditing) {
            setExpandedItemId(isExpanded ? null : item.Id);
          }
        }}
      >
        {/* Color strip */}
        {item.Color ? <View style={[s.colorStrip, { backgroundColor: item.Color }]} /> : null}

        {/* Main row */}
        <View style={s.todoCardInner}>
          {/* Checkbox */}
          <Animated.View style={{ transform: [{ scale }] }}>
            <TouchableOpacity
              style={item.IsDone ? s.checkboxChecked : s.checkboxUnchecked}
              onPress={() => handleToggleDone(item.Id)}
              activeOpacity={0.7}
              accessibilityLabel={`${item.Text || 'Untitled'}, ${item.IsDone ? 'completed' : 'not completed'}`}
              accessibilityRole="checkbox"
            >
              {item.IsDone && <Text style={s.checkboxCheckmark}>✓</Text>}
            </TouchableOpacity>
          </Animated.View>

          {/* Content */}
          <View style={s.todoTextContainer}>
            {isEditing ? (
              <TextInput
                style={s.todoTextInput}
                value={item.Text}
                onChangeText={(t) => handleUpdateItem(item.Id, { Text: t })}
                onBlur={() => setEditingItemId(null)}
                autoFocus
                multiline
              />
            ) : (
              <TouchableOpacity
                onPress={() => setEditingItemId(item.Id)}
                activeOpacity={0.7}
                accessibilityLabel={`Edit task: ${item.Text || 'Untitled'}`}
                accessibilityRole="button"
              >
                <Text style={item.IsDone ? s.todoTextDone : s.todoText}>
                  {item.Text || 'Untitled'}
                </Text>
              </TouchableOpacity>
            )}

            {/* Active timer countdown */}
            {activeTimers[item.Id] && (
              <View style={{
                flexDirection: 'row', alignItems: 'center', marginTop: 4,
                backgroundColor: '#FF444420', borderRadius: 8, paddingHorizontal: 8, paddingVertical: 3,
                alignSelf: 'flex-start',
              }}>
                <Text style={{ fontSize: 11, marginRight: 4 }}>⏱</Text>
                <Text style={{ color: '#FF6B6B', fontSize: 13, fontWeight: '600', fontVariant: ['tabular-nums'] }}>
                  {formatCountdown(activeTimers[item.Id].remaining)}
                </Text>
                <TouchableOpacity onPress={() => cancelTimer(item.Id)} style={{ marginLeft: 8 }} accessibilityLabel="Cancel timer" accessibilityRole="button" hitSlop={{ top: 8, bottom: 8, left: 8, right: 8 }}>
                  <Text style={{ color: '#FF4444', fontSize: 12, fontWeight: '600' }}>✕</Text>
                </TouchableOpacity>
              </View>
            )}

            {/* Metadata row */}
            <View style={s.metadataRow}>
              {renderPriorityBadge(item)}
              {renderDueDateChip(item)}
              {renderRecurrenceBadge(item)}
              {renderTags(item)}
              {/* Timer badge */}
              {item.TimerMinutes && !activeTimers[item.Id] && (
                <TouchableOpacity
                  style={{
                    flexDirection: 'row', alignItems: 'center',
                    backgroundColor: '#6384FF18', borderRadius: 8,
                    paddingHorizontal: 6, paddingVertical: 2, marginLeft: 4,
                  }}
                  onPress={() => startTimer(item.Id, item.TimerMinutes!)}
                  activeOpacity={0.7}
                >
                  <Text style={{ fontSize: 10 }}>⏱</Text>
                  <Text style={{ color: '#6384FF', fontSize: 11, fontWeight: '500', marginLeft: 2 }}>
                    {item.TimerMinutes}m
                  </Text>
                  <Text style={{ color: '#34D399', fontSize: 10, fontWeight: '600', marginLeft: 4 }}>▶</Text>
                </TouchableOpacity>
              )}
              {/* Reminder badge */}
              {item.ReminderAt && (
                <TouchableOpacity
                  style={{
                    flexDirection: 'row', alignItems: 'center',
                    backgroundColor: new Date(item.ReminderAt) > new Date() ? '#FBBF2418' : '#66668018',
                    borderRadius: 8, paddingHorizontal: 6, paddingVertical: 2, marginLeft: 4,
                  }}
                  onPress={() => handleOpenReminderPicker(item)}
                  activeOpacity={0.7}
                >
                  <Text style={{ fontSize: 10 }}>🔔</Text>
                  <Text style={{
                    color: new Date(item.ReminderAt) > new Date() ? '#FBBF24' : '#666680',
                    fontSize: 11, fontWeight: '500', marginLeft: 2,
                  }}>
                    {formatReminderDate(item.ReminderAt)}
                  </Text>
                </TouchableOpacity>
              )}
            </View>
          </View>

          {/* Expand chevron */}
          <TouchableOpacity
            style={s.expandChevron}
            onPress={() => setExpandedItemId(isExpanded ? null : item.Id)}
            accessibilityLabel={isExpanded ? 'Collapse task details' : 'Expand task details'}
            accessibilityRole="button"
            hitSlop={{ top: 8, bottom: 8, left: 8, right: 8 }}
          >
            <Text style={s.expandChevronText}>{isExpanded ? '▾' : '▸'}</Text>
          </TouchableOpacity>
        </View>

        {/* Expanded area */}
        {renderExpandedArea(item)}
      </TouchableOpacity>
    );
  }, [
    expandedItemId, editingItemId, handleToggleDone, handleDeleteItem,
    handleUpdateItem, handleCyclePriority, handleOpenDatePicker,
    handleCycleRecurrence, getCheckboxScale, showColorPicker, tagInputItemId,
    tagInputText, todoItems, editingSubtaskId, selectedDayKey,
    activeTimers, handleCycleTimer, startTimer, cancelTimer,
    handleOpenReminderPicker,
  ]);

  // ═══════════════════════════════════════════════════════
  // MAIN RENDER
  // ═══════════════════════════════════════════════════════

  const scrollY = useSharedValue(0);
  const scrollHandler = useAnimatedScrollHandler({ onScroll: (e) => { scrollY.value = e.contentOffset.y; } });

  return (
    <LinearGradient
      colors={[colors.bg.base, colors.bg.baseEnd]}
      style={s.container}
    >
      <View style={s.container}>
        <KeyboardAvoidingView
          style={s.container}
          behavior={Platform.OS === 'ios' ? 'padding' : undefined}
          keyboardVerticalOffset={Platform.OS === 'ios' ? 90 : 0}
        >
          {/* ─── Header ─── */}
          <ScreenHeader
            title="To-Do"
            subtitle="Tasks & Reminders"
            scrollY={scrollY}
            rightActions={
              <View style={s.headerRight}>
                {/* Sort button */}
                <TouchableOpacity
                  style={{
                    paddingHorizontal: 8, paddingVertical: 4,
                    borderRadius: 8,
                    backgroundColor: sortMode !== 'manual' ? colors.accent.primary + '22' : 'transparent',
                    marginRight: 6,
                  }}
                  onPress={() => setShowSortModal(true)}
                  activeOpacity={0.7}
                  accessibilityLabel="Sort tasks"
                  accessibilityRole="button"
                >
                  <Ionicons name="swap-vertical" size={18} color={sortMode !== 'manual' ? colors.accent.primary : colors.text.secondary} />
                </TouchableOpacity>
                {/* Templates button */}
                <TouchableOpacity
                  style={{
                    paddingHorizontal: 8, paddingVertical: 4,
                    borderRadius: 8,
                    marginRight: 6,
                  }}
                  onPress={() => setShowTemplateModal(true)}
                  activeOpacity={0.7}
                  accessibilityLabel="Task templates"
                  accessibilityRole="button"
                >
                  <Ionicons name="copy-outline" size={18} color={colors.text.secondary} />
                </TouchableOpacity>
                <View style={s.syncIndicator}>
                  <View style={[
                    s.syncDot,
                    {
                      backgroundColor: syncStatus === 'connected' ? colors.accent.success
                        : syncStatus === 'syncing' ? colors.accent.warning
                        : syncStatus === 'offline' ? colors.accent.error
                        : colors.text.disabled,
                    },
                  ]} />
                  <Text style={s.syncText}>
                    {syncStatus === 'connected' ? 'SYNCED' : syncStatus === 'syncing' ? 'SYNCING' : syncStatus === 'offline' ? 'OFFLINE' : 'IDLE'}
                  </Text>
                </View>
              </View>
            }
          />

          {/* ─── Day Selector ─── */}
          {renderDaySelector()}

          {/* ─── Search Bar ─── */}
          <View style={{
            flexDirection: 'row', alignItems: 'center',
            marginHorizontal: 16, marginTop: 8, marginBottom: 4,
            backgroundColor: colors.bg.input, borderRadius: 12,
            paddingHorizontal: 12,
          }}>
            <Text style={{ fontSize: 16, color: colors.text.tertiary, marginRight: 8 }}>🔍</Text>
            <TextInput
              style={{
                flex: 1, color: colors.text.primary,
                fontSize: 14, fontFamily: font.regular,
                paddingVertical: Platform.OS === 'ios' ? 10 : 8,
              }}
              value={searchQuery}
              onChangeText={(t) => {
                setSearchQuery(t);
                setIsSearchActive(t.trim().length > 0);
              }}
              placeholder="Search all tasks..."
              placeholderTextColor={colors.text.tertiary}
              returnKeyType="search"
              accessibilityLabel="Search all tasks"
              accessibilityRole="search"
            />
            {searchQuery.length > 0 && (
              <TouchableOpacity
                onPress={() => { setSearchQuery(''); setIsSearchActive(false); }}
                activeOpacity={0.7}
                accessibilityLabel="Clear search"
                accessibilityRole="button"
                hitSlop={{ top: 8, bottom: 8, left: 8, right: 8 }}
              >
                <Text style={{ fontSize: 14, color: colors.text.tertiary }}>✕</Text>
              </TouchableOpacity>
            )}
          </View>

          {/* ─── Search Results ─── */}
          {isSearchActive && searchQuery.trim().length > 0 && (
            <View style={{
              flex: 1, marginHorizontal: 16, marginTop: 4,
              backgroundColor: colors.bg.card, borderRadius: 12,
              padding: 12, maxHeight: 320,
            }}>
              {searchResults.length === 0 ? (
                <Text style={{ color: colors.text.tertiary, textAlign: 'center', paddingVertical: 20, fontFamily: font.regular }}>
                  No matching tasks found
                </Text>
              ) : (
                <FlatList
                  data={searchResults}
                  keyExtractor={(r, idx) => r.item.Id + idx}
                  showsVerticalScrollIndicator={false}
                  renderItem={({ item: result }) => {
                    const dayDate = new Date(result.day.Date);
                    return (
                      <TouchableOpacity
                        style={{
                          paddingVertical: 8, paddingHorizontal: 8,
                          borderBottomWidth: 1, borderBottomColor: colors.border.subtle,
                        }}
                        onPress={() => handleSearchResultTap(result.day.Date)}
                        activeOpacity={0.7}
                      >
                        <Text style={{ fontSize: 11, color: colors.accent.primary, fontFamily: font.medium, marginBottom: 2 }}>
                          {dayDate.toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' })}
                        </Text>
                        <Text style={{
                          color: result.item.IsDone ? colors.text.disabled : colors.text.primary,
                          fontFamily: font.regular, fontSize: 14,
                          textDecorationLine: result.item.IsDone ? 'line-through' : 'none',
                        }}>
                          {result.item.Text}
                        </Text>
                        {result.item.Description ? (
                          <Text style={{ color: colors.text.tertiary, fontSize: 12, fontFamily: font.regular, marginTop: 1 }} numberOfLines={1}>
                            {result.item.Description}
                          </Text>
                        ) : null}
                      </TouchableOpacity>
                    );
                  }}
                />
              )}
            </View>
          )}

          {/* ─── Summary ─── */}
          {totalCount > 0 && (
            <View style={s.summaryRow}>
              <Text style={s.summaryText}>
                <Text style={s.summaryCount}>{doneCount}</Text>/{totalCount} completed
              </Text>
              <Text style={s.summaryText}>
                {selectedDate.toLocaleDateString('en-US', { weekday: 'long', month: 'short', day: 'numeric' })}
              </Text>
            </View>
          )}

          {/* ─── Todo List ─── */}
          <View style={s.listContainer}>
            {todoItems.length === 0 ? (
              <View style={s.emptyState}>
                <Text style={s.emptyStateIcon}>📋</Text>
                <Text style={s.emptyStateTitle}>No tasks for this day</Text>
                <Text style={s.emptyStateSubtitle}>
                  Add a todo below to get started. Tasks sync with your PC automatically.
                </Text>
              </View>
            ) : (
              <FlashListCast
                data={todoItems}
                renderItem={renderTodoItem}
                estimatedItemSize={90}
                keyExtractor={(item: TodoItem) => item.Id}
                contentContainerStyle={s.listContent}
                showsVerticalScrollIndicator={false}
                extraData={extraDataMemo}
              />
            )}
          </View>

          {/* ─── Bottom Input Bar ─── */}
          <View style={s.inputBar}>
            <TextInput
              style={s.inputBarInput}
              value={newTodoText}
              onChangeText={setNewTodoText}
              placeholder="Add a new task..."
              placeholderTextColor={colors.text.tertiary}
              onSubmitEditing={handleAddTodo}
              returnKeyType="done"
              accessibilityLabel="New task text"
              accessibilityRole="text"
            />
            <TouchableOpacity
              style={[s.inputBarSend, !newTodoText.trim() && s.inputBarSendDisabled]}
              onPress={handleAddTodo}
              disabled={!newTodoText.trim()}
              activeOpacity={0.7}
              accessibilityLabel="Add task"
              accessibilityRole="button"
            >
              <Text style={{ color: newTodoText.trim() ? '#FFF' : colors.text.disabled, fontSize: 20, fontWeight: '300' }}>＋</Text>
            </TouchableOpacity>
          </View>

          {/* ─── Date Picker Modal ─── */}
          {showDatePicker && Platform.OS === 'ios' && (
            <Modal transparent animationType="slide" visible={showDatePicker}>
              <TouchableOpacity
                style={s.datePickerOverlay}
                activeOpacity={1}
                onPress={() => setShowDatePicker(false)}
              >
                <View style={s.datePickerContainer}>
                  <View style={s.datePickerHeader}>
                    <TouchableOpacity style={s.datePickerClearBtn} onPress={handleClearDueDate}>
                      <Text style={s.datePickerClearText}>Clear</Text>
                    </TouchableOpacity>
                    <Text style={s.datePickerTitle}>Due Date</Text>
                    <TouchableOpacity style={s.datePickerDoneBtn} onPress={handleDatePickerDone}>
                      <Text style={s.datePickerDoneText}>Done</Text>
                    </TouchableOpacity>
                  </View>
                  <DateTimePicker
                    value={datePickerValue}
                    mode="date"
                    display="spinner"
                    onChange={handleDateChange}
                    themeVariant="dark"
                  />
                </View>
              </TouchableOpacity>
            </Modal>
          )}

          {/* Android date picker (shown as dialog) */}
          {showDatePicker && Platform.OS === 'android' && (
            <DateTimePicker
              value={datePickerValue}
              mode="date"
              display="default"
              onChange={handleDateChange}
            />
          )}

          {/* ─── Reminder Picker Modal (iOS) ─── */}
          {showReminderPicker && Platform.OS === 'ios' && (
            <Modal transparent animationType="slide" visible={showReminderPicker}>
              <TouchableOpacity
                style={s.datePickerOverlay}
                activeOpacity={1}
                onPress={() => setShowReminderPicker(false)}
              >
                <View style={s.datePickerContainer}>
                  <View style={s.datePickerHeader}>
                    <TouchableOpacity style={s.datePickerClearBtn} onPress={handleClearReminder}>
                      <Text style={s.datePickerClearText}>Clear</Text>
                    </TouchableOpacity>
                    <Text style={s.datePickerTitle}>Set Reminder</Text>
                    <TouchableOpacity style={s.datePickerDoneBtn} onPress={handleReminderPickerDone}>
                      <Text style={s.datePickerDoneText}>Done</Text>
                    </TouchableOpacity>
                  </View>
                  <DateTimePicker
                    value={reminderPickerValue}
                    mode="datetime"
                    display="spinner"
                    onChange={handleReminderDateChange}
                    themeVariant="dark"
                    minimumDate={new Date()}
                  />
                </View>
              </TouchableOpacity>
            </Modal>
          )}

          {/* Reminder picker (Android — sequential date then time) */}
          {showReminderPicker && Platform.OS === 'android' && (
            <DateTimePicker
              value={reminderPickerValue}
              mode={reminderPickerMode}
              display="default"
              onChange={handleReminderDateChange}
              minimumDate={reminderPickerMode === 'date' ? new Date() : undefined}
            />
          )}

          {/* ─── Templates Modal ─── */}
          <Modal
            visible={showTemplateModal}
            transparent
            animationType="fade"
            onRequestClose={() => setShowTemplateModal(false)}
          >
            <TouchableOpacity
              style={{
                flex: 1, backgroundColor: 'rgba(0,0,0,0.6)',
                justifyContent: 'center', alignItems: 'center',
              }}
              activeOpacity={1}
              onPress={() => setShowTemplateModal(false)}
            >
              <View style={{
                width: '85%', backgroundColor: colors.bg.card,
                borderRadius: 16, padding: 20, maxHeight: '70%',
                borderWidth: 1, borderColor: colors.border.subtle,
              }}>
                <Text style={{
                  color: colors.text.primary, fontSize: 18,
                  fontFamily: font.semibold, marginBottom: 16, textAlign: 'center',
                }}>
                  📋 Todo Templates
                </Text>
                {TODO_TEMPLATES.map((tmpl, idx) => (
                  <TouchableOpacity
                    key={idx}
                    style={{
                      backgroundColor: colors.bg.input, borderRadius: 12,
                      padding: 14, marginBottom: 10,
                      borderWidth: 1, borderColor: colors.border.subtle,
                    }}
                    onPress={() => handleApplyTemplate(tmpl)}
                    activeOpacity={0.7}
                  >
                    <Text style={{
                      color: colors.text.primary, fontSize: 15,
                      fontFamily: font.semibold, marginBottom: 6,
                    }}>
                      {tmpl.name}
                    </Text>
                    <Text style={{
                      color: colors.text.secondary, fontSize: 12,
                      fontFamily: font.regular, lineHeight: 18,
                    }}>
                      {tmpl.items.join(' • ')}
                    </Text>
                  </TouchableOpacity>
                ))}
                <TouchableOpacity
                  style={{
                    alignSelf: 'center', paddingVertical: 10, paddingHorizontal: 24,
                    marginTop: 4,
                  }}
                  onPress={() => setShowTemplateModal(false)}
                  activeOpacity={0.7}
                >
                  <Text style={{ color: colors.text.tertiary, fontFamily: font.medium, fontSize: 14 }}>Cancel</Text>
                </TouchableOpacity>
              </View>
            </TouchableOpacity>
          </Modal>

          {/* ─── Sort Modal ─── */}
          <Modal
            visible={showSortModal}
            transparent
            animationType="fade"
            onRequestClose={() => setShowSortModal(false)}
          >
            <TouchableOpacity
              style={{
                flex: 1, backgroundColor: 'rgba(0,0,0,0.6)',
                justifyContent: 'center', alignItems: 'center',
              }}
              activeOpacity={1}
              onPress={() => setShowSortModal(false)}
            >
              <View style={{
                width: '75%', backgroundColor: colors.bg.card,
                borderRadius: 16, padding: 20,
                borderWidth: 1, borderColor: colors.border.subtle,
              }}>
                <Text style={{
                  color: colors.text.primary, fontSize: 18,
                  fontFamily: font.semibold, marginBottom: 16, textAlign: 'center',
                }}>
                  ↕ Sort Tasks
                </Text>
                {SORT_OPTIONS.map(opt => (
                  <TouchableOpacity
                    key={opt.mode}
                    style={{
                      flexDirection: 'row', alignItems: 'center',
                      backgroundColor: sortMode === opt.mode ? colors.accent.primary + '18' : colors.bg.input,
                      borderRadius: 10, padding: 12, marginBottom: 8,
                      borderWidth: 1,
                      borderColor: sortMode === opt.mode ? colors.accent.primary + '44' : colors.border.subtle,
                    }}
                    onPress={() => handleSelectSort(opt.mode)}
                    activeOpacity={0.7}
                  >
                    <Text style={{ fontSize: 16, marginRight: 10 }}>{opt.icon}</Text>
                    <Text style={{
                      color: sortMode === opt.mode ? colors.accent.primary : colors.text.primary,
                      fontFamily: font.medium, fontSize: 15, flex: 1,
                    }}>
                      {opt.label}
                    </Text>
                    {sortMode === opt.mode && (
                      <Text style={{ color: colors.accent.primary, fontSize: 16 }}>✓</Text>
                    )}
                  </TouchableOpacity>
                ))}
                <TouchableOpacity
                  style={{
                    alignSelf: 'center', paddingVertical: 10, paddingHorizontal: 24,
                    marginTop: 4,
                  }}
                  onPress={() => setShowSortModal(false)}
                  activeOpacity={0.7}
                >
                  <Text style={{ color: colors.text.tertiary, fontFamily: font.medium, fontSize: 14 }}>Cancel</Text>
                </TouchableOpacity>
              </View>
            </TouchableOpacity>
          </Modal>
        </KeyboardAvoidingView>
      </View>
    </LinearGradient>
  );
}

export default function TodoScreen() {
  return (
    <AppErrorBoundary fallbackTitle="Todo screen crashed">
      <TodoScreenInner />
    </AppErrorBoundary>
  );
}
