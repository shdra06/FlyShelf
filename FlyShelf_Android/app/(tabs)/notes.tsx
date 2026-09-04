import React, { useState, useEffect, useRef, useCallback, useMemo } from 'react';
import { useFocusEffect } from 'expo-router';
import AppErrorBoundary from '../../components/AppErrorBoundary';
import {
  View, Text, TextInput, TouchableOpacity, ScrollView, Alert,
  Animated, Keyboard, Platform, ToastAndroid, Share, Modal,
  ActivityIndicator, FlatList, DeviceEventEmitter,
} from 'react-native';
// SafeAreaView import removed — unused
import { LinearGradient } from 'expo-linear-gradient';
import * as Haptics from 'expo-haptics';
import AsyncStorage from '@react-native-async-storage/async-storage';
import EncryptedStorage from '../../utils/EncryptedStorage';

import { useSettings } from '../../context/SettingsContext';
import { fetchWithTimeout, resolveBestPcUrl } from '../../utils/networkHelpers';
import { NetworkClock } from '../../utils/networkClock';
import { getSecureItem } from '../../utils/secureStorage';
import { useNotesSync, NOTES_STORAGE_KEY } from '../../features/notes/useNotesSync';
import {
  NoteDay, NoteBullet, FreeformSection, SubBulletItem,
  createNoteDay, createNoteBullet, createFreeformSection,
  generateId, formatDisplayDate, isToday, parseDate,
} from '../../utils/noteTypes';
import { createNotesStyles } from '../../styles/notesStyles';
import { font, component } from '../../styles/theme';
import { useAppTheme } from '../../hooks/useAppTheme';
import { Ionicons } from '@expo/vector-icons';
import { fuzzyIsMatch, fuzzyScore } from '../../utils/textNormalize';
import { useSharedValue } from 'react-native-reanimated';
import ScreenHeader from '../../components/ScreenHeader';

// ═══════════════════════════════════════════════════════════
// CONSTANTS
// ═══════════════════════════════════════════════════════════

// NOTES_STORAGE_KEY, PENDING_NOTES_SYNC_KEY, POLL_INTERVAL, DEBOUNCE_POST_MS
// are now internal to features/notes/useNotesSync.ts
const RECENT_DAYS_COUNT = 30;
// First element '' means 'no color' / default (transparent strip)
const BULLET_COLORS = ['', '#6384FF', '#34D399', '#F87171', '#FBBF24', '#A78BFA', '#F472B6', '#60A5FA'];

const NOTE_TEMPLATES = [
  { name: '🛒 Grocery List', bullets: ['Fruits & Vegetables', 'Dairy & Eggs', 'Meat & Fish', 'Bread & Bakery', 'Snacks & Beverages', 'Household Items'] },
  { name: '🧑‍💻 Daily Standup', bullets: ['What I did yesterday', 'What I will do today', 'Blockers / Challenges'] },
  { name: '📋 Meeting Notes', bullets: ['Meeting Title & Date', 'Attendees', 'Agenda Items', 'Discussion Points', 'Action Items', 'Next Steps'] },
  { name: '✈️ Travel Packing', bullets: ['Documents (passport, tickets)', 'Clothing', 'Toiletries', 'Electronics & Chargers', 'Medications', 'Misc'] },
  { name: '📊 Project Planner', bullets: ['Project Goal', 'Key Milestones', 'Resources Needed', 'Timeline', 'Risks & Mitigations'] },
  { name: '📅 Week Review', bullets: ['Wins this week', 'Challenges faced', 'Lessons learned', 'Goals for next week', 'Grateful for'] },
  { name: '📖 Reading Notes', bullets: ['Book/Article Title', 'Author', 'Key Takeaways', 'Favorite Quotes', 'How to Apply'] },
  { name: '🍳 Recipe', bullets: ['Recipe Name', 'Ingredients', 'Preparation Steps', 'Cooking Time & Temperature', 'Notes & Tips'] },
];

// ═══════════════════════════════════════════════════════════
// HELPERS
// ═══════════════════════════════════════════════════════════

// Helper functions moved to utils or replaced by centralized discovery

/** Safe haptic feedback — silently swallows errors on unsupported devices */
const safeHaptic = (style = Haptics.ImpactFeedbackStyle.Light) => {
  try { Haptics.impactAsync(style); } catch {}
};

/** Format time as "h:mm a" */
const formatTime = (dateStr: string): string => {
  if (!dateStr) return '';
  const d = new Date(dateStr);
  if (isNaN(d.getTime())) return '';
  const h = d.getHours();
  const m = d.getMinutes().toString().padStart(2, '0');
  const ampm = h >= 12 ? 'PM' : 'AM';
  const h12 = h % 12 || 12;
  return `${h12}:${m} ${ampm}`;
};

/** Generate date strings for the last N days */
const generateRecentDates = (count: number): string[] => {
  const dates: string[] = [];
  const today = new Date();
  for (let i = 0; i < count; i++) {
    const d = new Date(today);
    d.setDate(today.getDate() - i);
    dates.push(d.toISOString().split('T')[0] + 'T00:00:00');
  }
  return dates;
};

/** Normalize PC date keys like "2026-09-03T00:00:00.0000000+05:30" to "2026-09-03T00:00:00" */
const normalizeDateKey = (dateStr: string): string => {
  if (!dateStr) return dateStr;
  // Extract just YYYY-MM-DD from any ISO format
  const datePart = dateStr.split('T')[0];
  return datePart + 'T00:00:00';
};

/** Find or create a NoteDay for a given date key */
const ensureDay = (days: NoteDay[], dateKey: string): { days: NoteDay[]; day: NoteDay; idx: number } => {
  const idx = days.findIndex(d => d.Date === dateKey);
  if (idx >= 0) return { days, day: days[idx], idx };
  const newDay = createNoteDay(parseDate(dateKey));
  newDay.Date = dateKey;
  const updated = [...days, newDay];
  return { days: updated, day: newDay, idx: updated.length - 1 };
};

import { toast } from '../../context/ToastContext';

// Unified toast notification helper
const showToast = (msg: string) => {
  if (msg.toLowerCase().includes('deleted')) {
    toast.info('Note Deleted', msg);
  } else if (msg.toLowerCase().includes('applied') || msg.toLowerCase().includes('saved')) {
    toast.success('Notes Updated', msg);
  } else if (msg.toLowerCase().includes('cannot') || msg.toLowerCase().includes('no ')) {
    toast.warning('Notes Notice', msg);
  } else {
    toast.info('Notes', msg);
  }
};

// ─── ONE-PAGE NOTEBOOK PAPER EDITOR ───
const PageModeEditor = React.memo(({
  day,
  dateKey,
  onSavePage,
  zoom,
  colors,
  styles,
  onOpenTemplates,
}: {
  day: NoteDay | undefined;
  dateKey: string;
  onSavePage: (content: string) => void;
  zoom: number;
  colors: any;
  styles: any;
  onOpenTemplates: () => void;
}) => {
  const initialContent = useMemo(() => {
    if (!day) return '';
    if (day.FreeformSections && day.FreeformSections.length > 0) {
      return day.FreeformSections.map(s => s.Content).filter(Boolean).join('\n\n');
    }
    if (day.Bullets && day.Bullets.length > 0) {
      return day.Bullets.map(b => {
        let line = '';
        if (b.Header) line += `${b.Header}\n`;
        if (b.Content) line += `${b.Content}\n`;
        if (b.SubBullets && b.SubBullets.length > 0) {
          line += b.SubBullets.map(s => `  ${s.IsDone ? '✓' : '•'} ${s.Text}`).join('\n') + '\n';
        }
        return line.trim();
      }).filter(Boolean).join('\n\n');
    }
    return '';
  }, [day?.Date, day?.LastModified]);

  const [text, setText] = useState(initialContent);
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const prevDateKey = useRef(dateKey);

  useEffect(() => {
    if (dateKey !== prevDateKey.current) {
      prevDateKey.current = dateKey;
      setText(initialContent);
    } else if (initialContent !== text && !debounceRef.current) {
      setText(initialContent);
    }
  }, [dateKey, initialContent]);

  const handleChange = (val: string) => {
    setText(val);
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => {
      onSavePage(val);
      debounceRef.current = null;
    }, 350);
  };

  const handleInsert = (token: string) => {
    safeHaptic();
    const updated = text ? `${text}\n${token}` : token;
    setText(updated);
    if (debounceRef.current) clearTimeout(debounceRef.current);
    onSavePage(updated);
  };

  const displayDateStr = useMemo(() => {
    const d = parseDate(dateKey);
    return d.toLocaleDateString('en-US', { weekday: 'long', month: 'short', day: 'numeric', year: 'numeric' });
  }, [dateKey]);

  return (
    <ScrollView
      style={styles.pageContainer}
      contentContainerStyle={styles.listContent}
      showsVerticalScrollIndicator={false}
      keyboardShouldPersistTaps="handled"
    >
      <View style={styles.pagePaper}>
        <View style={styles.pagePaperHeader}>
          <View style={styles.pagePaperTitleRow}>
            <View style={styles.pagePaperDateBadge}>
              <Ionicons name="calendar-outline" size={14 * zoom} color={colors.accent.primary} />
              <Text style={[styles.pagePaperDateText, { fontSize: 12 * zoom }]}>{displayDateStr}</Text>
            </View>
            <TouchableOpacity onPress={onOpenTemplates} hitSlop={{ top: 8, bottom: 8, left: 8, right: 8 }} accessibilityLabel="Note templates" accessibilityRole="button">
              <Text style={{ fontSize: 16 }}>📋</Text>
            </TouchableOpacity>
          </View>
        </View>

        <View style={styles.pagePaperBody}>
          <TextInput
            style={[
              styles.pagePaperInput,
              {
                fontSize: 15 * zoom,
                lineHeight: 24 * zoom,
                color: colors.text.primary,
              },
            ]}
            value={text}
            onChangeText={handleChange}
            placeholder="Tap to start writing today's notes... Press Enter for new lines."
            placeholderTextColor={colors.text.tertiary}
            multiline
            scrollEnabled={false}
            autoCapitalize="sentences"
            textAlignVertical="top"
          />
        </View>

        <View style={styles.pageQuickBar}>
          <TouchableOpacity style={styles.pageQuickBtn} onPress={() => handleInsert('• ')} accessibilityLabel="Add bullet point" accessibilityRole="button">
            <Text style={[styles.pageQuickBtnText, { fontSize: 11 * zoom }]}>• Bullet</Text>
          </TouchableOpacity>
          <TouchableOpacity style={styles.pageQuickBtn} onPress={() => handleInsert('[ ] ')} accessibilityLabel="Add checkbox" accessibilityRole="button">
            <Text style={[styles.pageQuickBtnText, { fontSize: 11 * zoom }]}>✓ Box</Text>
          </TouchableOpacity>
          <TouchableOpacity style={styles.pageQuickBtn} onPress={() => handleInsert('## ')} accessibilityLabel="Add heading" accessibilityRole="button">
            <Text style={[styles.pageQuickBtnText, { fontSize: 11 * zoom }]}># Title</Text>
          </TouchableOpacity>
          <TouchableOpacity style={styles.pageQuickBtn} onPress={() => { safeHaptic(); onSavePage(text); toast.success('Saved', 'Page saved to local storage'); }} accessibilityLabel="Save note" accessibilityRole="button">
            <Text style={[styles.pageQuickBtnText, { color: colors.accent.primary, fontWeight: '700', fontSize: 11 * zoom }]}>💾 Save</Text>
          </TouchableOpacity>
        </View>
      </View>
    </ScrollView>
  );
});

// ═══════════════════════════════════════════════════════════
// MAIN SCREEN
// ═══════════════════════════════════════════════════════════
function NotesScreenInner() {
  const { colors, shadows } = useAppTheme();
  const styles = useMemo(() => createNotesStyles(colors, shadows), [colors, shadows]);
  const { deviceName } = useSettings();

  // ─── Sync hook (PC polling + debounced POST) ───
  const {
    days, setDays, syncStatus, isLoading,
    modifiedDatesRef, schedulePost, schedulePostRef, daysRef, triggerImmediateFetch,
  } = useNotesSync();

  // Listen to WebSocket push events from index.tsx
  useEffect(() => {
    const sub = DeviceEventEmitter.addListener('notes_changed', () => {
      triggerImmediateFetch();
    });
    return () => {
      sub.remove();
    };
  }, [triggerImmediateFetch]);

  // ─── Zoom & View Mode State ───
  const [noteZoom, setNoteZoom] = useState(1.0);
  const [noteMode, setNoteMode] = useState<'page' | 'bullet' | 'freeform'>('page');

  useEffect(() => {
    (async () => {
      try {
        const savedZoom = await AsyncStorage.getItem('@flyshelf_notes_zoom');
        if (savedZoom) {
          const parsed = parseFloat(savedZoom);
          if (!isNaN(parsed) && parsed >= 0.7 && parsed <= 2.0) setNoteZoom(parsed);
        }
        const savedMode = await AsyncStorage.getItem('@flyshelf_notes_view_mode');
        if (savedMode === 'page' || savedMode === 'bullet' || savedMode === 'freeform') {
          setNoteMode(savedMode);
        }
      } catch {}
    })();
  }, []);

  const handleZoomChange = useCallback((delta: number) => {
    setNoteZoom(prev => {
      const next = Math.min(1.6, Math.max(0.8, parseFloat((prev + delta).toFixed(2))));
      AsyncStorage.setItem('@flyshelf_notes_zoom', String(next)).catch(() => {});
      return next;
    });
    safeHaptic();
  }, []);

  const handleSetMode = useCallback((mode: 'page' | 'bullet' | 'freeform') => {
    safeHaptic(Haptics.ImpactFeedbackStyle.Medium);
    setNoteMode(mode);
    AsyncStorage.setItem('@flyshelf_notes_view_mode', mode).catch(() => {});
  }, []);

  // ─── UI-only state ───
  const [selectedDateIdx, setSelectedDateIdx] = useState(0);
  const [deletingBulletId, setDeletingBulletId] = useState<string | null>(null);
  const [editingTagBulletId, setEditingTagBulletId] = useState<string | null>(null);
  const [newTagText, setNewTagText] = useState('');
  const [showColorPicker, setShowColorPicker] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [isSearching, setIsSearching] = useState(false);
  const [showTemplates, setShowTemplates] = useState(false);

  // ─── UI refs ───
  const fabScale = useRef(new Animated.Value(1)).current;

  // ─── Generated date chips (refreshes after midnight + on tab focus) ───
  const [todayKey, setTodayKey] = useState(new Date().toISOString().split('T')[0]);
  useEffect(() => {
    const check = () => {
      const now = new Date().toISOString().split('T')[0];
      if (now !== todayKey) setTodayKey(now);
    };
    const interval = setInterval(check, 60000);
    return () => clearInterval(interval);
  }, [todayKey]);

  // ─── Reset to today when Notes tab is focused (fixes stale date across midnight) ───
  useFocusEffect(useCallback(() => {
    const now = new Date().toISOString().split('T')[0];
    if (now !== todayKey) {
      setTodayKey(now);
      setSelectedDateIdx(0); // Jump back to today
    }
  }, [todayKey]));

  const recentDates = useMemo(() => generateRecentDates(RECENT_DAYS_COUNT), [todayKey]);
  const selectedDateKey = recentDates[selectedDateIdx];

  // ─── Current day data ───
  const currentDay = days.find(d => d.Date === selectedDateKey);
  const isFreeformMode = noteMode === 'freeform' || (currentDay?.IsFreeformMode ?? false);

  // ═══════════════════════════════════════════════════════════
  // EDIT HELPERS
  // ═══════════════════════════════════════════════════════════

  /** Update days state + mark date as modified + persist immediately + schedule POST */
  const updateDays = useCallback((updater: (prev: NoteDay[]) => NoteDay[], dateKey?: string) => {
    setDays(prev => {
      const updated = updater(prev);
      daysRef.current = updated;
      EncryptedStorage.setItem(NOTES_STORAGE_KEY, JSON.stringify(updated)).catch(() => {});
      return updated;
    });
    if (dateKey) {
      modifiedDatesRef.current.add(dateKey);
    }
    schedulePost();
  }, [schedulePost, setDays, daysRef, modifiedDatesRef]);

  /** Update a specific bullet in the current day */
  const updateBullet = useCallback((bulletId: string, updater: (b: NoteBullet) => NoteBullet) => {
    updateDays(prev => {
      return prev.map(day => {
        if (day.Date !== selectedDateKey) return day;
        return {
          ...day,
          LastModified: NetworkClock.now(),
          Bullets: (day.Bullets || []).map(b => b.Id === bulletId ? updater({ ...b, LastEdited: new Date().toISOString(), LastEditedByDevice: deviceName || 'Android' }) : b),
        };
      });
    }, selectedDateKey);
  }, [selectedDateKey, updateDays, deviceName]);

  /** Update a specific freeform section in the current day */
  const updateFreeformSection = useCallback((sectionId: string, content: string) => {
    updateDays(prev => {
      return prev.map(day => {
        if (day.Date !== selectedDateKey) return day;
        return {
          ...day,
          LastModified: NetworkClock.now(),
          FreeformSections: (day.FreeformSections || []).map(s =>
            s.Id === sectionId ? { ...s, Content: content } : s
          ),
        };
      });
    }, selectedDateKey);
  }, [selectedDateKey, updateDays]);

  /** Save entire page text (Page View mode) */
  const handleSavePage = useCallback((content: string) => {
    updateDays(prev => {
      const { days: updated, day, idx } = ensureDay(prev, selectedDateKey);
      const curSections = day.FreeformSections || [];
      const sectionId = curSections[0]?.Id || generateId();
      const updatedSections: FreeformSection[] = [
        {
          Id: sectionId,
          Content: content,
          CreatedAt: curSections[0]?.CreatedAt || new Date().toISOString(),
          SubNotes: curSections[0]?.SubNotes || [],
        }
      ];
      updated[idx] = {
        ...day,
        FreeformSections: updatedSections,
        LastModified: NetworkClock.now(),
      };
      return updated;
    }, selectedDateKey);
  }, [selectedDateKey, updateDays]);

  // ═══════════════════════════════════════════════════════════
  // ACTIONS
  // ═══════════════════════════════════════════════════════════

  const handleAddBullet = useCallback(() => {
    safeHaptic(Haptics.ImpactFeedbackStyle.Medium);
    const newBullet = createNoteBullet();
    newBullet.CreatedByDevice = deviceName || 'Android';
    newBullet.LastEditedByDevice = deviceName || 'Android';
    updateDays(prev => {
      const { days: updated, day, idx } = ensureDay(prev, selectedDateKey);
      const sorted = [...(day.Bullets || []), { ...newBullet, SortOrder: (day.Bullets || []).length }];
      updated[idx] = { ...day, Bullets: sorted, LastModified: NetworkClock.now() };
      return updated;
    }, selectedDateKey);
  }, [selectedDateKey, updateDays, deviceName]);

  const handleAddFreeformSection = useCallback((afterIdx?: number) => {
    safeHaptic();
    const section = createFreeformSection();
    updateDays(prev => {
      const { days: updated, day, idx } = ensureDay(prev, selectedDateKey);
      const sections = [...(day.FreeformSections || [])];
      if (afterIdx !== undefined) {
        sections.splice(afterIdx + 1, 0, section);
      } else {
        sections.push(section);
      }
      updated[idx] = { ...day, FreeformSections: sections, LastModified: NetworkClock.now() };
      return updated;
    }, selectedDateKey);
  }, [selectedDateKey, updateDays]);

  const handleDeleteBullet = useCallback((bulletId: string) => {
    safeHaptic(Haptics.ImpactFeedbackStyle.Heavy);
    updateDays(prev => {
      return prev.map(day => {
        if (day.Date !== selectedDateKey) return day;
        return {
          ...day,
          LastModified: NetworkClock.now(),
          Bullets: (day.Bullets || []).filter(b => b.Id !== bulletId),
        };
      });
    }, selectedDateKey);
    setDeletingBulletId(null);
    showToast('Note deleted');
  }, [selectedDateKey, updateDays]);

  const handleTogglePin = useCallback((bulletId: string) => {
    safeHaptic();
    updateBullet(bulletId, b => ({ ...b, IsPinned: !b.IsPinned }));
  }, [updateBullet]);

  const handleAddTag = useCallback((bulletId: string, tag: string) => {
    if (!tag.trim()) return;
    updateBullet(bulletId, b => ({
      ...b,
      Tags: [...(b.Tags || []), tag.trim()],
    }));
    setEditingTagBulletId(null);
    setNewTagText('');
  }, [updateBullet]);

  const handleRemoveTag = useCallback((bulletId: string, tagIdx: number) => {
    safeHaptic();
    updateBullet(bulletId, b => ({
      ...b,
      Tags: b.Tags.filter((_, i) => i !== tagIdx),
    }));
  }, [updateBullet]);

  const handleToggleSubBullet = useCallback((bulletId: string, subId: string) => {
    safeHaptic();
    updateBullet(bulletId, b => ({
      ...b,
      SubBullets: (b.SubBullets || []).map(s =>
        s.Id === subId ? { ...s, IsDone: !s.IsDone } : s
      ),
    }));
  }, [updateBullet]);

  const handleAddSubBullet = useCallback((bulletId: string) => {
    safeHaptic();
    const sub: SubBulletItem = { Id: generateId(), Text: '', IsDone: false };
    updateBullet(bulletId, b => ({
      ...b,
      SubBullets: [...(b.SubBullets || []), sub],
    }));
  }, [updateBullet]);

  const handleUpdateSubBulletText = useCallback((bulletId: string, subId: string, text: string) => {
    updateBullet(bulletId, b => ({
      ...b,
      SubBullets: (b.SubBullets || []).map(s =>
        s.Id === subId ? { ...s, Text: text } : s
      ),
    }));
  }, [updateBullet]);

  const handleSetColor = useCallback((bulletId: string, color: string) => {
    safeHaptic();
    updateBullet(bulletId, b => ({ ...b, Color: color }));
    setShowColorPicker(null);
  }, [updateBullet]);

  const handleToggleMode = useCallback(() => {
    safeHaptic(Haptics.ImpactFeedbackStyle.Medium);
    updateDays(prev => {
      const { days: updated, day, idx } = ensureDay(prev, selectedDateKey);
      updated[idx] = { ...day, IsFreeformMode: !day.IsFreeformMode, LastModified: NetworkClock.now() };
      return updated;
    }, selectedDateKey);
  }, [selectedDateKey, updateDays]);

  const handleSelectDay = useCallback((idx: number) => {
    safeHaptic();
    setSelectedDateIdx(idx);
    setDeletingBulletId(null);
    setEditingTagBulletId(null);
    setShowColorPicker(null);
    setSearchQuery('');
    setIsSearching(false);
  }, []);

  // ─── Search across all days (AUDIT FIX: fuzzy matching + relevance sorting) ───
  const searchResults = React.useMemo(() => {
    if (!searchQuery.trim()) return [];
    const q = searchQuery.trim();
    const results: { dateKey: string; displayDate: string; bullet: NoteBullet | null; freeform: FreeformSection | null; score: number }[] = [];

    for (const day of days) {
      const dDate = parseDate(day.Date);
      const displayDate = dDate.toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' });

      // Search bullets
      for (const b of day.Bullets || []) {
        const haystack = [b.Header, b.Content, ...(b.Tags || [])].join(' ');
        if (fuzzyIsMatch(q, haystack)) {
          const s = fuzzyScore(q, haystack);
          results.push({ dateKey: day.Date, displayDate, bullet: b, freeform: null, score: s });
        }
      }

      // Search freeform sections
      for (const s of day.FreeformSections || []) {
        const haystack = [s.Title || '', s.Content || ''].join(' ');
        if (fuzzyIsMatch(q, haystack)) {
          const sc = fuzzyScore(q, haystack);
          results.push({ dateKey: day.Date, displayDate, bullet: null, freeform: s, score: sc });
        }
      }
    }

    // Sort by relevance score descending
    results.sort((a, b) => b.score - a.score);
    return results;
  }, [searchQuery, days]);

  // ─── Apply template ───
  const handleApplyTemplate = useCallback((template: typeof NOTE_TEMPLATES[0]) => {
    safeHaptic(Haptics.ImpactFeedbackStyle.Medium);
    updateDays(prev => {
      const { days: updated, day, idx } = ensureDay(prev, selectedDateKey);
      const existingCount = (day.Bullets || []).length;
      const newBullets = template.bullets.map((text, i) => {
        const b = createNoteBullet(text);
        b.Header = text;
        b.Content = '';
        b.SortOrder = existingCount + i;
        return b;
      });
      updated[idx] = { ...day, Bullets: [...(day.Bullets || []), ...newBullets], LastModified: NetworkClock.now() };
      return updated;
    }, selectedDateKey);
    setShowTemplates(false);
    showToast('Template applied');
  }, [selectedDateKey, updateDays]);

  // ─── Export / Share ───
  const handleExport = useCallback(() => {
    if (!currentDay) {
      showToast('No notes to export');
      return;
    }
    const dDate = parseDate(selectedDateKey);
    const dateLabel = dDate.toLocaleDateString('en-US', { month: 'long', day: 'numeric', year: 'numeric' });

    Alert.alert('Export Notes', 'Choose a format', [
      {
        text: 'Export as Markdown',
        onPress: () => {
          let md = `# Notes — ${dateLabel}\n\n`;
          for (const b of currentDay.Bullets || []) {
            if (b.Header) md += `## ${b.Header}\n`;
            if (b.Content) md += `${b.Content}\n`;
            if (b.Tags && b.Tags.length > 0) md += `*Tags: ${b.Tags.join(', ')}*\n`;
            md += '\n';
          }
          for (const s of currentDay.FreeformSections || []) {
            if (s.Content && s.Content.trim()) {
              md += '---\n' + s.Content + '\n\n';
            }
          }
          Share.share({ message: md.trim() }).catch(() => {});
        },
      },
      {
        text: 'Export as Plain Text',
        onPress: () => {
          let txt = `Notes — ${dateLabel}\n${'─'.repeat(24)}\n\n`;
          for (const b of currentDay.Bullets || []) {
            if (b.Header) txt += `• ${b.Header}\n`;
            if (b.Content) txt += `  ${b.Content}\n`;
            txt += '\n';
          }
          for (const s of currentDay.FreeformSections || []) {
            if (s.Content && s.Content.trim()) {
              txt += s.Content + '\n\n';
            }
          }
          Share.share({ message: txt.trim() }).catch(() => {});
        },
      },
      { text: 'Cancel', style: 'cancel' },
    ]);
  }, [currentDay, selectedDateKey]);

  // FAB press animation
  const handleFabPressIn = useCallback(() => {
    Animated.spring(fabScale, { toValue: 0.9, useNativeDriver: true, damping: 15, stiffness: 200 }).start();
  }, [fabScale]);

  const handleFabPressOut = useCallback(() => {
    Animated.spring(fabScale, { toValue: 1, useNativeDriver: true, damping: 15, stiffness: 200 }).start();
  }, [fabScale]);

  // ═══════════════════════════════════════════════════════════
  // RENDER: Bullet Card
  // ═══════════════════════════════════════════════════════════

  const renderBulletCard = useCallback(({ item }: { item: NoteBullet }) => {
    const isDeleting = deletingBulletId === item.Id;
    const isEditingTag = editingTagBulletId === item.Id;
    const isColorPicking = showColorPicker === item.Id;
    const accentColor = item.Color || 'transparent';

    return (
      <TouchableOpacity
        activeOpacity={0.95}
        onLongPress={() => {
          safeHaptic(Haptics.ImpactFeedbackStyle.Heavy);
          setDeletingBulletId(item.Id);
        }}
        delayLongPress={400}
        accessibilityLabel={`Note: ${item.Header || 'Untitled'}. Long press to delete`}
        accessibilityRole="button"
      >
        <View style={styles.bulletCard}>
          <View style={styles.bulletCardInner}>
            {/* Color accent strip */}
            <View style={[styles.colorStrip, { backgroundColor: accentColor }]} />

            <View style={styles.bulletBody}>
              {/* Header row */}
              <View style={styles.bulletHeader}>
                {item.IsPinned && (
                  <TouchableOpacity
                    style={styles.pinIndicator}
                    onPress={() => handleTogglePin(item.Id)}
                    accessibilityLabel="Unpin note"
                    accessibilityRole="button"
                  >
                    <Text style={styles.pinText}>📌</Text>
                  </TouchableOpacity>
                )}
                <TextInput
                  style={[styles.bulletHeaderInput, { fontSize: 15 * noteZoom, lineHeight: 20 * noteZoom }]}
                  value={item.Header}
                  onChangeText={text => updateBullet(item.Id, b => ({ ...b, Header: text }))}
                  placeholder="Title..."
                  placeholderTextColor={colors.text.tertiary}
                  returnKeyType="next"
                  accessibilityLabel="Note title"
                  accessibilityRole="text"
                />
                {!item.IsPinned && (
                  <TouchableOpacity
                    style={styles.pinIndicator}
                    onPress={() => handleTogglePin(item.Id)}
                    hitSlop={{ top: 8, bottom: 8, left: 8, right: 8 }}
                    accessibilityLabel="Pin note"
                    accessibilityRole="button"
                  >
                    <Text style={[styles.pinText, { opacity: 0.3 }]}>📌</Text>
                  </TouchableOpacity>
                )}
              </View>

              {/* Content */}
              <TextInput
                style={[styles.bulletContent, { fontSize: 14 * noteZoom, lineHeight: 20 * noteZoom }]}
                value={item.Content}
                onChangeText={text => updateBullet(item.Id, b => ({ ...b, Content: text }))}
                placeholder="Write something..."
                placeholderTextColor={colors.text.tertiary}
                multiline
                accessibilityLabel="Note content"
                accessibilityRole="text"
              />

              {/* Tags */}
              <View style={styles.bulletMeta}>
                {(item.Tags || []).map((tag, i) => (
                  <TouchableOpacity
                    key={`${tag}-${i}`}
                    style={styles.tagPill}
                    onLongPress={() => handleRemoveTag(item.Id, i)}
                    accessibilityLabel={`Tag: ${tag}. Long press to remove`}
                    accessibilityRole="button"
                  >
                    <Text style={[styles.tagPillText, { fontSize: 10 * noteZoom }]}>{tag}</Text>
                  </TouchableOpacity>
                ))}
                {isEditingTag ? (
                  <TextInput
                    style={[styles.tagInput, { fontSize: 10 * noteZoom }]}
                    value={newTagText}
                    onChangeText={setNewTagText}
                    onSubmitEditing={() => handleAddTag(item.Id, newTagText)}
                    onBlur={() => { setEditingTagBulletId(null); setNewTagText(''); }}
                    placeholder="tag"
                    placeholderTextColor={colors.text.tertiary}
                    autoFocus
                    returnKeyType="done"
                  />
                ) : (
                  <TouchableOpacity
                    style={styles.addTagButton}
                    onPress={() => { setEditingTagBulletId(item.Id); setNewTagText(''); }}
                    accessibilityLabel="Add tag"
                    accessibilityRole="button"
                  >
                    <Text style={[styles.addTagText, { fontSize: 10 * noteZoom }]}>+ tag</Text>
                  </TouchableOpacity>
                )}
              </View>

              {/* Sub-bullets */}
              {item.SubBullets && item.SubBullets.length > 0 && (
                <View style={styles.subBulletsContainer}>
                  {item.SubBullets.map(sub => (
                    <View key={sub.Id} style={styles.subBulletRow}>
                      <TouchableOpacity
                        style={[
                          styles.subBulletCheckbox,
                          sub.IsDone && styles.subBulletCheckboxDone,
                        ]}
                        onPress={() => handleToggleSubBullet(item.Id, sub.Id)}
                        accessibilityLabel={`Sub-item: ${sub.Text || 'Untitled'}, ${sub.IsDone ? 'completed' : 'not completed'}`}
                        accessibilityRole="checkbox"
                      >
                        {sub.IsDone && <Text style={styles.subBulletCheckmark}>✓</Text>}
                      </TouchableOpacity>
                      <TextInput
                        style={[
                          styles.subBulletText,
                          sub.IsDone && styles.subBulletTextDone,
                          { fontSize: 13 * noteZoom },
                        ]}
                        value={sub.Text}
                        onChangeText={text => handleUpdateSubBulletText(item.Id, sub.Id, text)}
                        placeholder="Sub-item..."
                        placeholderTextColor={colors.text.tertiary}
                      />
                    </View>
                  ))}
                </View>
              )}

              {/* Add sub-bullet */}
              <TouchableOpacity
                style={styles.addSubBulletButton}
                onPress={() => handleAddSubBullet(item.Id)}
                accessibilityLabel="Add sub-item"
                accessibilityRole="button"
              >
                <Text style={[styles.addSubBulletText, { color: colors.accent.primary }]}>+</Text>
                <Text style={styles.addSubBulletText}>sub-item</Text>
              </TouchableOpacity>

              {/* Footer: time + color */}
              <View style={styles.bulletFooter}>
                <Text style={styles.bulletTime}>{formatTime(item.LastEdited)}</Text>
                <View style={styles.bulletActions}>
                  <TouchableOpacity
                    style={styles.bulletActionButton}
                    onPress={() => setShowColorPicker(isColorPicking ? null : item.Id)}
                    accessibilityLabel={isColorPicking ? 'Close color picker' : 'Change note color'}
                    accessibilityRole="button"
                    hitSlop={{ top: 8, bottom: 8, left: 8, right: 8 }}
                  >
                    <View style={[styles.colorDot, { backgroundColor: item.Color || colors.text.tertiary }]} />
                  </TouchableOpacity>
                </View>
              </View>

              {/* Color picker row */}
              {isColorPicking && (
                <View style={styles.colorRow}>
                  {BULLET_COLORS.map((c, i) => (
                    <TouchableOpacity
                      key={i}
                      style={[
                        styles.colorOption,
                        { backgroundColor: c || colors.bg.input },
                        item.Color === c && styles.colorOptionSelected,
                      ]}
                      onPress={() => handleSetColor(item.Id, c)}
                      accessibilityLabel={`Color option ${i + 1}${item.Color === c ? ', selected' : ''}`}
                      accessibilityRole="button"
                    />
                  ))}
                </View>
              )}
            </View>
          </View>

          {/* Delete overlay */}
          {isDeleting && (
            <View style={styles.deleteOverlay}>
              <View style={styles.deleteOverlayRow}>
                <TouchableOpacity
                  style={styles.deleteButton}
                  onPress={() => handleDeleteBullet(item.Id)}
                  accessibilityLabel="Delete note"
                  accessibilityRole="button"
                >
                  <Text style={styles.deleteButtonText}>Delete</Text>
                </TouchableOpacity>
                <TouchableOpacity
                  style={styles.cancelButton}
                  onPress={() => setDeletingBulletId(null)}
                  accessibilityLabel="Cancel delete"
                  accessibilityRole="button"
                >
                  <Text style={styles.cancelButtonText}>Cancel</Text>
                </TouchableOpacity>
              </View>
            </View>
          )}
        </View>
      </TouchableOpacity>
    );
  }, [
    colors, deletingBulletId, editingTagBulletId, newTagText, showColorPicker,
    updateBullet, handleTogglePin, handleAddTag, handleRemoveTag,
    handleToggleSubBullet, handleAddSubBullet, handleUpdateSubBulletText,
    handleDeleteBullet, handleSetColor,
  ]);

  // ═══════════════════════════════════════════════════════════
  // RENDER: Freeform Section Card
  // ═══════════════════════════════════════════════════════════

  // Derive freeformData early so renderFreeformCard can reference it (fix #2)
  const freeformData = currentDay?.FreeformSections || [];

  const handleDeleteFreeformSection = useCallback((sectionId: string) => {
    // Use daysRef to avoid stale closure over `days` (fix #5)
    const curDay = daysRef.current.find(d => d.Date === selectedDateKey);
    if (!curDay || (curDay.FreeformSections?.length || 0) <= 1) {
      showToast('Cannot remove the last section');
      return;
    }
    safeHaptic();
    updateDays(prev => prev.map(day => {
      if (day.Date !== selectedDateKey) return day;
      return {
        ...day,
        FreeformSections: (day.FreeformSections || []).filter(s => s.Id !== sectionId),
        LastModified: NetworkClock.now(),
      };
    }), selectedDateKey);
  }, [selectedDateKey, updateDays]);

  // AH-6: Debounced freeform TextInput wrapper — uses local state + 300ms debounce to avoid
  // triggering main state updates (and sync) on every keystroke
  const DebouncedFreeformInput = useMemo(() => {
    return React.memo(({ sectionId, initialContent, onDebouncedChange, placeholder, placeholderTextColor, style }: {
      sectionId: string; initialContent: string;
      onDebouncedChange: (id: string, text: string) => void;
      placeholder: string; placeholderTextColor: string; style: any;
    }) => {
      const [localText, setLocalText] = React.useState(initialContent);
      const debounceTimerRef = React.useRef<ReturnType<typeof setTimeout> | null>(null);
      // Keep local text in sync if the external value changes (e.g. from sync)
      const prevContentRef = React.useRef(initialContent);
      React.useEffect(() => {
        if (initialContent !== prevContentRef.current) {
          prevContentRef.current = initialContent;
          setLocalText(initialContent);
        }
      }, [initialContent]);
      const handleChange = React.useCallback((text: string) => {
        setLocalText(text);
        if (debounceTimerRef.current) clearTimeout(debounceTimerRef.current);
        debounceTimerRef.current = setTimeout(() => {
          onDebouncedChange(sectionId, text);
        }, 300);
      }, [sectionId, onDebouncedChange]);
      // Flush on unmount only — use ref to avoid re-running effect on every keystroke
      const latestTextRef = React.useRef(localText);
      React.useEffect(() => { latestTextRef.current = localText; }, [localText]);
      React.useEffect(() => {
        return () => {
          if (debounceTimerRef.current) {
            onDebouncedChange(sectionId, latestTextRef.current);
            clearTimeout(debounceTimerRef.current);
          }
        };
      }, [sectionId, onDebouncedChange]);
      return (
        <TextInput
          style={style}
          value={localText}
          onChangeText={handleChange}
          placeholder={placeholder}
          placeholderTextColor={placeholderTextColor}
          multiline
        />
      );
    });
  }, []);

  const renderFreeformCard = useCallback(({ item, index }: { item: FreeformSection; index: number }) => (
    <View>
      <View style={styles.freeformCard}>
        <DebouncedFreeformInput
          sectionId={item.Id}
          initialContent={item.Content}
          onDebouncedChange={updateFreeformSection}
          placeholder="Start writing..."
          placeholderTextColor={colors.text.tertiary}
          style={[styles.freeformInput, { fontSize: 15 * noteZoom, lineHeight: 22 * noteZoom }]}
        />
        <View style={styles.freeformMeta}>
          <Text style={styles.freeformTime}>
            {formatTime(item.CreatedAt)}
          </Text>
          {(freeformData.length > 1) && (
            <TouchableOpacity
              onPress={() => handleDeleteFreeformSection(item.Id)}
              hitSlop={{ top: 8, bottom: 8, left: 8, right: 8 }}
              accessibilityLabel="Delete freeform section"
              accessibilityRole="button"
              style={{ marginLeft: 8, padding: 4 }}
            >
              <Ionicons name="trash-outline" size={16} color={colors.text.tertiary} />
            </TouchableOpacity>
          )}
        </View>
      </View>
      {/* Add section button between cards */}
      <TouchableOpacity
        style={styles.addSectionButton}
        onPress={() => handleAddFreeformSection(index)}
        accessibilityLabel="Add freeform section"
        accessibilityRole="button"
      >
        <Text style={[styles.addSectionText, { color: colors.accent.primary }]}>+</Text>
        <Text style={styles.addSectionText}>section</Text>
      </TouchableOpacity>
    </View>
  ), [colors, updateFreeformSection, handleAddFreeformSection, handleDeleteFreeformSection, freeformData.length, DebouncedFreeformInput, noteZoom]);

  // ═══════════════════════════════════════════════════════════
  // RENDER: Empty State
  // ═══════════════════════════════════════════════════════════

  const renderEmpty = () => (
    <View style={styles.emptyState}>
      <Text style={styles.emptyIcon}>{noteMode === 'freeform' ? '📝' : '✏️'}</Text>
      <Text style={styles.emptyTitle}>No notes for this day</Text>
      <Text style={styles.emptySubtitle}>
        Tap the + button to add your first{'\n'}
        {noteMode === 'freeform' ? 'freeform section' : 'bullet note'}
      </Text>
    </View>
  );

  // ═══════════════════════════════════════════════════════════
  // RENDER: Day Chips
  // ═══════════════════════════════════════════════════════════

  const renderDayChips = () => (
    <View style={styles.daySelectorContainer}>
      <ScrollView
        horizontal
        showsHorizontalScrollIndicator={false}
        contentContainerStyle={styles.daySelectorScroll}
      >
        {recentDates.map((dateKey, idx) => {
          const d = parseDate(dateKey);
          const dayNum = d.getDate();
          const month = d.toLocaleDateString('en-US', { month: 'short' });
          const isActive = idx === selectedDateIdx;
          const isTodayDate = isToday(dateKey);
          const hasContent = days.some(
            day => day.Date === dateKey &&
            ((day.Bullets && day.Bullets.length > 0) ||
             (day.FreeformSections && day.FreeformSections.some(s => s.Content?.trim())))
          );

          return (
            <TouchableOpacity
              key={dateKey}
              style={[
                styles.dayChip,
                isActive && styles.dayChipActive,
                isTodayDate && !isActive && styles.dayChipToday,
              ]}
              onPress={() => handleSelectDay(idx)}
              activeOpacity={0.7}
              accessibilityLabel={`${month} ${dayNum}${isTodayDate ? ', today' : ''}${isActive ? ', selected' : ''}${hasContent ? ', has notes' : ''}`}
              accessibilityRole="tab"
            >
              <Text style={[
                styles.dayChipNumber,
                isActive && styles.dayChipNumberActive,
              ]}>
                {dayNum}
              </Text>
              <Text style={[
                styles.dayChipMonth,
                isActive && styles.dayChipMonthActive,
              ]}>
                {month}
              </Text>
              {hasContent && <View style={styles.dayChipDot} />}
            </TouchableOpacity>
          );
        })}
      </ScrollView>
    </View>
  );

  // ═══════════════════════════════════════════════════════════
  // RENDER: Main
  // ═══════════════════════════════════════════════════════════

  const bulletData = currentDay?.Bullets || [];
  // Sort pinned first, then by SortOrder (M-10: memoized to avoid re-sort every render)
  const sortedBullets = useMemo(() => [...bulletData].sort((a, b) => {
    if (a.IsPinned && !b.IsPinned) return -1;
    if (!a.IsPinned && b.IsPinned) return 1;
    return (a.SortOrder || 0) - (b.SortOrder || 0);
  }), [bulletData]);

  // freeformData is declared earlier (near line ~828) so renderFreeformCard can use it

  const syncColor = syncStatus === 'synced'
    ? colors.accent.success
    : syncStatus === 'syncing'
    ? colors.accent.warning
    : colors.text.tertiary;

  // ─── Templates Modal ───
  const renderTemplatesModal = () => (
    <Modal
      visible={showTemplates}
      transparent
      animationType="fade"
      onRequestClose={() => setShowTemplates(false)}
    >
      <TouchableOpacity
        style={{ flex: 1, backgroundColor: 'rgba(0,0,0,0.6)', justifyContent: 'center', alignItems: 'center' }}
        activeOpacity={1}
        onPress={() => setShowTemplates(false)}
        accessibilityLabel="Close templates"
        accessibilityRole="button"
      >
        <TouchableOpacity activeOpacity={1} style={{
          width: '85%', maxHeight: '70%', backgroundColor: colors.bg.elevated,
          borderRadius: 16, borderWidth: 1, borderColor: colors.border.medium,
          overflow: 'hidden',
        }}>
          <View style={{ paddingHorizontal: 20, paddingVertical: 16, borderBottomWidth: 1, borderBottomColor: colors.border.subtle }}>
            <Text style={{ color: colors.text.primary, fontSize: 18, fontWeight: '700' }}>📋  Note Templates</Text>
            <Text style={{ color: colors.text.tertiary, fontSize: 12, marginTop: 4 }}>Tap to add bullets to the selected day's note</Text>
          </View>
          <ScrollView style={{ paddingHorizontal: 8, paddingVertical: 8 }}>
            {NOTE_TEMPLATES.map((tpl, i) => (
              <TouchableOpacity
                key={i}
                style={{
                  paddingHorizontal: 16, paddingVertical: 14,
                  borderRadius: 10, marginVertical: 2,
                  backgroundColor: colors.bg.card,
                }}
                activeOpacity={0.7}
                onPress={() => handleApplyTemplate(tpl)}
                accessibilityLabel={`Apply template: ${tpl.name}`}
                accessibilityRole="button"
              >
                <Text style={{ color: colors.text.primary, fontSize: 15, fontWeight: '600' }}>{tpl.name}</Text>
                <Text style={{ color: colors.text.tertiary, fontSize: 12, marginTop: 3 }} numberOfLines={1}>
                  {tpl.bullets.join(' · ')}
                </Text>
              </TouchableOpacity>
            ))}
          </ScrollView>
        </TouchableOpacity>
      </TouchableOpacity>
    </Modal>
  );

  // ─── Search Results View ───
  const renderSearchResults = () => {
    // Group results by date
    const grouped: Record<string, typeof searchResults> = {};
    for (const r of searchResults) {
      if (!grouped[r.dateKey]) grouped[r.dateKey] = [];
      grouped[r.dateKey].push(r);
    }
    const groups = Object.entries(grouped);

    if (groups.length === 0) {
      return (
        <View style={styles.emptyState}>
          <Text style={styles.emptyIcon}>🔍</Text>
          <Text style={styles.emptyTitle}>No matches found</Text>
          <Text style={styles.emptySubtitle}>Try a different search term</Text>
        </View>
      );
    }

    return (
      <ScrollView
        contentContainerStyle={{ paddingHorizontal: 16, paddingBottom: 100 }}
        showsVerticalScrollIndicator={false}
        keyboardShouldPersistTaps="handled"
        onScroll={scrollHandler}
        scrollEventThrottle={16}
      >
        {groups.map(([dateKey, items]) => {
          const dayIdx = recentDates.indexOf(dateKey);
          return (
            <View key={dateKey} style={{ marginBottom: 12 }}>
              {/* Day header */}
              <TouchableOpacity
                onPress={() => { if (dayIdx >= 0) handleSelectDay(dayIdx); }}
                style={{ paddingVertical: 8 }}
                accessibilityLabel={`Go to ${items[0].displayDate}`}
                accessibilityRole="button"
              >
                <Text style={{ color: colors.accent.primary, fontSize: 13, fontWeight: '700', letterSpacing: 0.5 }}>
                  {items[0].displayDate.toUpperCase()}
                </Text>
              </TouchableOpacity>
              {/* Results */}
              {items.map((r, i) => (
                <TouchableOpacity
                  key={i}
                  style={{
                    backgroundColor: colors.bg.card, borderRadius: 10, padding: 12, marginBottom: 6,
                    borderWidth: 1, borderColor: colors.border.subtle,
                  }}
                  activeOpacity={0.7}
                  onPress={() => { if (dayIdx >= 0) handleSelectDay(dayIdx); }}
                >
                  {r.bullet ? (
                    <>
                      {r.bullet.Header ? (
                        <Text style={{ color: colors.text.primary, fontSize: 14 * noteZoom, fontWeight: '600' }} numberOfLines={1}>{r.bullet.Header}</Text>
                      ) : null}
                      {r.bullet.Content ? (
                        <Text style={{ color: colors.text.secondary, fontSize: 13 * noteZoom, marginTop: 2 }} numberOfLines={2}>{r.bullet.Content}</Text>
                      ) : null}
                      {r.bullet.Tags && r.bullet.Tags.length > 0 ? (
                        <Text style={{ color: colors.accent.primary, fontSize: 11, marginTop: 4 }}>{r.bullet.Tags.join(', ')}</Text>
                      ) : null}
                    </>
                  ) : r.freeform ? (
                    <Text style={{ color: colors.text.secondary, fontSize: 13 * noteZoom, fontStyle: 'italic' }} numberOfLines={3}>{r.freeform.Content}</Text>
                  ) : null}
                </TouchableOpacity>
              ))}
            </View>
          );
        })}
      </ScrollView>
    );
  };

  const scrollY = useSharedValue(0);
  const scrollHandler = (e: any) => {
    const offsetY = e?.nativeEvent?.contentOffset?.y;
    if (typeof offsetY === 'number') {
      scrollY.value = offsetY;
    }
  };

  return (
    <View style={styles.container}>
      {renderTemplatesModal()}
      <LinearGradient
        colors={[colors.bg.base, colors.bg.baseEnd, colors.bg.base]}
        style={styles.gradient}
      >
        <View style={styles.gradient}>
          {/* ─── Header ─── */}
          <ScreenHeader
            title="Notes"
            scrollY={scrollY}
            rightActions={
              <View style={styles.headerRight}>
                {/* Sync indicator */}
                <View style={styles.syncIndicator}>
                  <View style={[styles.syncDot, { backgroundColor: syncColor }]} />
                  <Text style={[styles.syncText, { color: syncColor }]}>
                    {syncStatus === 'synced' ? 'SYNCED' : syncStatus === 'syncing' ? 'SYNC' : 'OFFLINE'}
                  </Text>
                </View>

                {/* Zoom Controls */}
                <View style={styles.zoomContainer}>
                  <TouchableOpacity
                    style={styles.zoomBtn}
                    onPress={() => handleZoomChange(-0.15)}
                    accessibilityLabel="Zoom out text"
                    accessibilityRole="button"
                    hitSlop={{ top: 6, bottom: 6, left: 6, right: 6 }}
                  >
                    <Text style={styles.zoomBtnText}>A-</Text>
                  </TouchableOpacity>
                  <TouchableOpacity
                    onPress={() => { setNoteZoom(1.0); AsyncStorage.setItem('@flyshelf_notes_zoom', '1.0'); safeHaptic(); }}
                    accessibilityLabel="Reset zoom"
                    accessibilityRole="button"
                  >
                    <Text style={styles.zoomValueText}>{Math.round(noteZoom * 100)}%</Text>
                  </TouchableOpacity>
                  <TouchableOpacity
                    style={styles.zoomBtn}
                    onPress={() => handleZoomChange(0.15)}
                    accessibilityLabel="Zoom in text"
                    accessibilityRole="button"
                    hitSlop={{ top: 6, bottom: 6, left: 6, right: 6 }}
                  >
                    <Text style={styles.zoomBtnText}>A+</Text>
                  </TouchableOpacity>
                </View>

                {/* Export button */}
                <TouchableOpacity
                  style={{ padding: 6, marginRight: 2 }}
                  onPress={handleExport}
                  hitSlop={{ top: 8, bottom: 8, left: 8, right: 8 }}
                  accessibilityLabel="Export notes"
                  accessibilityRole="button"
                >
                  <Text style={{ fontSize: 16, color: colors.text.secondary }}>↗</Text>
                </TouchableOpacity>

                {/* Mode toggle */}
                <View style={styles.modeToggle}>
                  <TouchableOpacity
                    style={[styles.modeButton, noteMode === 'page' && styles.modeButtonActive]}
                    onPress={() => handleSetMode('page')}
                    activeOpacity={0.7}
                    accessibilityLabel="Page mode"
                    accessibilityRole="tab"
                  >
                    <Text style={[styles.modeButtonText, noteMode === 'page' && styles.modeButtonTextActive]}>
                      Page
                    </Text>
                  </TouchableOpacity>
                  <TouchableOpacity
                    style={[styles.modeButton, noteMode === 'bullet' && styles.modeButtonActive]}
                    onPress={() => handleSetMode('bullet')}
                    activeOpacity={0.7}
                    accessibilityLabel="Cards mode"
                    accessibilityRole="tab"
                  >
                    <Text style={[styles.modeButtonText, noteMode === 'bullet' && styles.modeButtonTextActive]}>
                      Cards
                    </Text>
                  </TouchableOpacity>
                  <TouchableOpacity
                    style={[styles.modeButton, noteMode === 'freeform' && styles.modeButtonActive]}
                    onPress={() => handleSetMode('freeform')}
                    activeOpacity={0.7}
                    accessibilityLabel="Freeform mode"
                    accessibilityRole="tab"
                  >
                    <Text style={[styles.modeButtonText, noteMode === 'freeform' && styles.modeButtonTextActive]}>
                      Free
                    </Text>
                  </TouchableOpacity>
                </View>
              </View>
            }
          />
          {/* ─── Day Selector ─── */}
          {renderDayChips()}

          {/* ─── Search Bar ─── */}
          <View style={{
            paddingHorizontal: 16, paddingTop: 6, paddingBottom: 8,
          }}>
            <View style={{
              flexDirection: 'row', alignItems: 'center',
              backgroundColor: colors.bg.input, borderRadius: 10,
              borderWidth: 1, borderColor: isSearching ? colors.border.accent : colors.border.subtle,
              paddingHorizontal: 12, height: 40,
            }}>
              <Text style={{ fontSize: 14, color: colors.text.tertiary, marginRight: 8 }}>🔍</Text>
              <TextInput
                style={{ flex: 1, color: colors.text.primary, fontSize: 14, paddingVertical: 0 }}
                value={searchQuery}
                onChangeText={(text) => { setSearchQuery(text); setIsSearching(text.length > 0); }}
                placeholder="Search all notes..."
                placeholderTextColor={colors.text.tertiary}
                returnKeyType="search"
                onFocus={() => setIsSearching(searchQuery.length > 0)}
                accessibilityLabel="Search all notes"
                accessibilityRole="search"
              />
              {searchQuery.length > 0 && (
                <TouchableOpacity
                  onPress={() => { setSearchQuery(''); setIsSearching(false); }}
                  hitSlop={{ top: 8, bottom: 8, left: 8, right: 8 }}
                  accessibilityLabel="Clear search"
                  accessibilityRole="button"
                >
                  <Text style={{ fontSize: 16, color: colors.text.tertiary }}>✕</Text>
                </TouchableOpacity>
              )}
            </View>
          </View>

          {/* ─── Content ─── */}
          <View style={styles.contentArea}>
            {isLoading ? (
              <View style={{ flex: 1, justifyContent: 'center', alignItems: 'center' }}>
                <ActivityIndicator size="large" color={colors.accent.primary} />
              </View>
            ) : isSearching && searchQuery.trim() ? (
              renderSearchResults()
            ) : noteMode === 'page' ? (
              <PageModeEditor
                day={currentDay}
                dateKey={selectedDateKey}
                onSavePage={handleSavePage}
                zoom={noteZoom}
                colors={colors}
                styles={styles}
                onOpenTemplates={() => setShowTemplates(true)}
              />
            ) : noteMode === 'freeform' ? (
              // Freeform mode
              freeformData.length === 0 ? (
                renderEmpty()
              ) : (
                <FlatList
                  data={freeformData}
                  renderItem={renderFreeformCard}
                  keyExtractor={(item: FreeformSection) => item.Id}
                  contentContainerStyle={styles.listContent}
                  showsVerticalScrollIndicator={false}
                  onScroll={scrollHandler}
                  scrollEventThrottle={16}
                  keyboardShouldPersistTaps="handled"
                  initialNumToRender={10}
                  maxToRenderPerBatch={10}
                  windowSize={7}
                  removeClippedSubviews={Platform.OS === 'android'}
                />
              )
            ) : (
              // Bullet mode
              sortedBullets.length === 0 ? (
                renderEmpty()
              ) : (
                <FlatList
                  data={sortedBullets}
                  renderItem={renderBulletCard}
                  keyExtractor={(item: NoteBullet) => item.Id}
                  extraData={`${deletingBulletId}-${editingTagBulletId}-${showColorPicker}-${noteZoom}`}
                  contentContainerStyle={styles.listContent}
                  showsVerticalScrollIndicator={false}
                  onScroll={scrollHandler}
                  scrollEventThrottle={16}
                  keyboardShouldPersistTaps="handled"
                  initialNumToRender={12}
                  maxToRenderPerBatch={12}
                  windowSize={7}
                  removeClippedSubviews={Platform.OS === 'android'}
                />
              )
            )}
          </View>

          {/* ─── FAB (visible in cards/freeform mode) ─── */}
          {noteMode !== 'page' && (
            <Animated.View style={[styles.fab, { transform: [{ scale: fabScale }] }]}>
              <TouchableOpacity
                onPress={noteMode === 'freeform' ? () => handleAddFreeformSection() : handleAddBullet}
                onPressIn={handleFabPressIn}
                onPressOut={handleFabPressOut}
                activeOpacity={0.9}
                style={{ width: '100%', height: '100%', justifyContent: 'center', alignItems: 'center' }}
                accessibilityLabel={noteMode === 'freeform' ? 'Add freeform section' : 'Add bullet note'}
                accessibilityRole="button"
              >
                <Text style={styles.fabText}>+</Text>
              </TouchableOpacity>
            </Animated.View>
          )}
        </View>
      </LinearGradient>
    </View>
  );
}

export default function NotesScreen() {
  return (
    <AppErrorBoundary fallbackTitle="Notes screen crashed">
      <NotesScreenInner />
    </AppErrorBoundary>
  );
}
