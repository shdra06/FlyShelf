import React, { useState, useEffect, useRef, useCallback } from 'react';
import {
  View, Text, TextInput, TouchableOpacity, ScrollView, Alert,
  Animated, Keyboard, Platform, ToastAndroid, Share, Modal,
} from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { FlashList } from '@shopify/flash-list';
const FlashListCast = FlashList as any;
import { LinearGradient } from 'expo-linear-gradient';
import * as Haptics from 'expo-haptics';
import AsyncStorage from '@react-native-async-storage/async-storage';

import { useSettings } from '../../context/SettingsContext';
import { fetchWithTimeout } from '../../utils/networkHelpers';
import { getSecureItem } from '../../utils/secureStorage';
import {
  NoteDay, NoteBullet, FreeformSection, SubBulletItem,
  createNoteDay, createNoteBullet, createFreeformSection,
  generateId, formatDisplayDate, isToday, parseDate,
} from '../../utils/noteTypes';
import { styles } from '../../styles/notesStyles';
import { colors, font } from '../../styles/theme';

// ═══════════════════════════════════════════════════════════
// CONSTANTS
// ═══════════════════════════════════════════════════════════

const NOTES_STORAGE_KEY = '@flyshelf_notes';
const POLL_INTERVAL = 10_000;
const DEBOUNCE_POST_MS = 2_000;
const RECENT_DAYS_COUNT = 30;
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

/** Resolve the best PC URL from cached pairing data */
const getCachedPcUrl = async (): Promise<string | null> => {
  try {
    // Try Cloudflare tunnel first (works from anywhere)
    const globalUrl = await getSecureItem('pairedGlobalUrl');
    if (globalUrl && globalUrl !== 'offline') {
      const url = globalUrl.trim().replace(/\/$/, '');
      if (url) return url;
    }
    // Fallback to local IP
    const localIp = await AsyncStorage.getItem('@pcLocalIp');
    if (localIp) {
      let url = localIp.trim();
      if (!url.startsWith('http')) url = `http://${url}`;
      const hostPart = url.replace(/^https?:\/\//, '');
      if (!hostPart.includes(':')) url = `${url}:8999`;
      return url.replace(/\/$/, '');
    }
  } catch {}
  return null;
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

/** Find or create a NoteDay for a given date key */
const ensureDay = (days: NoteDay[], dateKey: string): { days: NoteDay[]; day: NoteDay; idx: number } => {
  const idx = days.findIndex(d => d.Date === dateKey);
  if (idx >= 0) return { days, day: days[idx], idx };
  const newDay = createNoteDay(parseDate(dateKey));
  newDay.Date = dateKey;
  const updated = [...days, newDay];
  return { days: updated, day: newDay, idx: updated.length - 1 };
};

const showToast = (msg: string) => {
  if (Platform.OS === 'android') {
    ToastAndroid.show(msg, ToastAndroid.SHORT);
  }
};

// ═══════════════════════════════════════════════════════════
// MAIN SCREEN
// ═══════════════════════════════════════════════════════════
export default function NotesScreen() {
  const { pairingKey, deviceName } = useSettings();

  // ─── State ───
  const [days, setDays] = useState<NoteDay[]>([]);
  const [selectedDateIdx, setSelectedDateIdx] = useState(0);
  const [syncStatus, setSyncStatus] = useState<'synced' | 'syncing' | 'offline'>('offline');
  const [deletingBulletId, setDeletingBulletId] = useState<string | null>(null);
  const [editingTagBulletId, setEditingTagBulletId] = useState<string | null>(null);
  const [newTagText, setNewTagText] = useState('');
  const [showColorPicker, setShowColorPicker] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [isSearching, setIsSearching] = useState(false);
  const [showTemplates, setShowTemplates] = useState(false);

  // ─── Refs ───
  const debounceTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const pollTimerRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const pcUrlRef = useRef<string | null>(null);
  const daysRef = useRef<NoteDay[]>([]);
  const modifiedDatesRef = useRef<Set<string>>(new Set());
  const fabScale = useRef(new Animated.Value(1)).current;

  // Keep ref in sync
  useEffect(() => { daysRef.current = days; }, [days]);

  // ─── Generated date chips ───
  const recentDates = useRef(generateRecentDates(RECENT_DAYS_COUNT)).current;
  const selectedDateKey = recentDates[selectedDateIdx];

  // ─── Current day data ───
  const currentDay = days.find(d => d.Date === selectedDateKey);
  const isFreeformMode = currentDay?.IsFreeformMode ?? false;

  // ═══════════════════════════════════════════════════════════
  // SYNC: Load cached notes & resolve PC URL
  // ═══════════════════════════════════════════════════════════
  useEffect(() => {
    (async () => {
      // Load cached notes
      try {
        const stored = await AsyncStorage.getItem(NOTES_STORAGE_KEY);
        if (stored) {
          const parsed: NoteDay[] = JSON.parse(stored);
          if (Array.isArray(parsed) && parsed.length > 0) {
            setDays(parsed);
          }
        }
      } catch {}

      // Resolve PC URL
      pcUrlRef.current = await getCachedPcUrl();
    })();
  }, []);

  // ═══════════════════════════════════════════════════════════
  // SYNC: Poll for remote notes
  // ═══════════════════════════════════════════════════════════
  const fetchRemoteNotes = useCallback(async () => {
    if (!pcUrlRef.current || !pairingKey) {
      setSyncStatus('offline');
      return;
    }

    try {
      setSyncStatus('syncing');
      const res = await fetchWithTimeout(`${pcUrlRef.current}/api/notes`, {
        method: 'GET',
        headers: {
          'X-FlyShelf-Client': 'MobileCompanion',
          'X-Pairing-Key': pairingKey,
        },
      }, 5000);

      if (!res.ok) {
        setSyncStatus('offline');
        return;
      }

      const remoteDays: NoteDay[] = await res.json();
      if (!Array.isArray(remoteDays)) {
        setSyncStatus('offline');
        return;
      }

      // Merge: remote wins if LastModified is newer
      setDays(prev => {
        const merged = [...prev];
        for (const remote of remoteDays) {
          const localIdx = merged.findIndex(d => d.Date === remote.Date);
          if (localIdx >= 0) {
            const localMod = merged[localIdx].LastModified || 0;
            const remoteMod = remote.LastModified || 0;
            if (remoteMod > localMod) {
              merged[localIdx] = remote;
            }
          } else {
            merged.push(remote);
          }
        }
        // Persist after merge
        AsyncStorage.setItem(NOTES_STORAGE_KEY, JSON.stringify(merged)).catch(() => {});
        return merged;
      });

      setSyncStatus('synced');
    } catch {
      setSyncStatus('offline');
    }
  }, [pairingKey]);

  // Start polling
  useEffect(() => {
    // Initial fetch
    fetchRemoteNotes();

    pollTimerRef.current = setInterval(fetchRemoteNotes, POLL_INTERVAL);
    return () => {
      if (pollTimerRef.current) clearInterval(pollTimerRef.current);
    };
  }, [fetchRemoteNotes]);

  // ═══════════════════════════════════════════════════════════
  // SYNC: Debounced POST of modified days
  // ═══════════════════════════════════════════════════════════
  const schedulePost = useCallback(() => {
    if (debounceTimerRef.current) clearTimeout(debounceTimerRef.current);
    debounceTimerRef.current = setTimeout(async () => {
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
          },
          body: JSON.stringify(toPost),
        }, 5000);

        if (res.ok) {
          modifiedDatesRef.current = new Set();
          setSyncStatus('synced');
        } else {
          setSyncStatus('offline');
        }
      } catch {
        setSyncStatus('offline');
      }
    }, DEBOUNCE_POST_MS);
  }, [pairingKey]);

  // ═══════════════════════════════════════════════════════════
  // EDIT HELPERS
  // ═══════════════════════════════════════════════════════════

  /** Update days state + mark date as modified + persist + schedule POST */
  const updateDays = useCallback((updater: (prev: NoteDay[]) => NoteDay[], dateKey?: string) => {
    setDays(prev => {
      const updated = updater(prev);
      AsyncStorage.setItem(NOTES_STORAGE_KEY, JSON.stringify(updated)).catch(() => {});
      return updated;
    });
    if (dateKey) {
      modifiedDatesRef.current.add(dateKey);
    }
    schedulePost();
  }, [schedulePost]);

  /** Update a specific bullet in the current day */
  const updateBullet = useCallback((bulletId: string, updater: (b: NoteBullet) => NoteBullet) => {
    updateDays(prev => {
      return prev.map(day => {
        if (day.Date !== selectedDateKey) return day;
        return {
          ...day,
          LastModified: Date.now(),
          Bullets: day.Bullets.map(b => b.Id === bulletId ? updater({ ...b, LastEdited: new Date().toISOString() }) : b),
        };
      });
    }, selectedDateKey);
  }, [selectedDateKey, updateDays]);

  /** Update a specific freeform section in the current day */
  const updateFreeformSection = useCallback((sectionId: string, content: string) => {
    updateDays(prev => {
      return prev.map(day => {
        if (day.Date !== selectedDateKey) return day;
        return {
          ...day,
          LastModified: Date.now(),
          FreeformSections: (day.FreeformSections || []).map(s =>
            s.Id === sectionId ? { ...s, Content: content } : s
          ),
        };
      });
    }, selectedDateKey);
  }, [selectedDateKey, updateDays]);

  // ═══════════════════════════════════════════════════════════
  // ACTIONS
  // ═══════════════════════════════════════════════════════════

  const handleAddBullet = useCallback(() => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    const newBullet = createNoteBullet();
    updateDays(prev => {
      const { days: updated, day, idx } = ensureDay(prev, selectedDateKey);
      const sorted = [...day.Bullets, { ...newBullet, SortOrder: day.Bullets.length }];
      updated[idx] = { ...day, Bullets: sorted, LastModified: Date.now() };
      return updated;
    }, selectedDateKey);
  }, [selectedDateKey, updateDays]);

  const handleAddFreeformSection = useCallback((afterIdx?: number) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const section = createFreeformSection();
    updateDays(prev => {
      const { days: updated, day, idx } = ensureDay(prev, selectedDateKey);
      const sections = [...(day.FreeformSections || [])];
      if (afterIdx !== undefined) {
        sections.splice(afterIdx + 1, 0, section);
      } else {
        sections.push(section);
      }
      updated[idx] = { ...day, FreeformSections: sections, LastModified: Date.now() };
      return updated;
    }, selectedDateKey);
  }, [selectedDateKey, updateDays]);

  const handleDeleteBullet = useCallback((bulletId: string) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Heavy);
    updateDays(prev => {
      return prev.map(day => {
        if (day.Date !== selectedDateKey) return day;
        return {
          ...day,
          LastModified: Date.now(),
          Bullets: day.Bullets.filter(b => b.Id !== bulletId),
        };
      });
    }, selectedDateKey);
    setDeletingBulletId(null);
    showToast('Note deleted');
  }, [selectedDateKey, updateDays]);

  const handleTogglePin = useCallback((bulletId: string) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
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
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    updateBullet(bulletId, b => ({
      ...b,
      Tags: b.Tags.filter((_, i) => i !== tagIdx),
    }));
  }, [updateBullet]);

  const handleToggleSubBullet = useCallback((bulletId: string, subId: string) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    updateBullet(bulletId, b => ({
      ...b,
      SubBullets: b.SubBullets.map(s =>
        s.Id === subId ? { ...s, IsDone: !s.IsDone } : s
      ),
    }));
  }, [updateBullet]);

  const handleAddSubBullet = useCallback((bulletId: string) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const sub: SubBulletItem = { Id: generateId(), Text: '', IsDone: false };
    updateBullet(bulletId, b => ({
      ...b,
      SubBullets: [...b.SubBullets, sub],
    }));
  }, [updateBullet]);

  const handleUpdateSubBulletText = useCallback((bulletId: string, subId: string, text: string) => {
    updateBullet(bulletId, b => ({
      ...b,
      SubBullets: b.SubBullets.map(s =>
        s.Id === subId ? { ...s, Text: text } : s
      ),
    }));
  }, [updateBullet]);

  const handleSetColor = useCallback((bulletId: string, color: string) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    updateBullet(bulletId, b => ({ ...b, Color: color }));
    setShowColorPicker(null);
  }, [updateBullet]);

  const handleToggleMode = useCallback(() => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    updateDays(prev => {
      const { days: updated, day, idx } = ensureDay(prev, selectedDateKey);
      updated[idx] = { ...day, IsFreeformMode: !day.IsFreeformMode, LastModified: Date.now() };
      return updated;
    }, selectedDateKey);
  }, [selectedDateKey, updateDays]);

  const handleSelectDay = useCallback((idx: number) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    setSelectedDateIdx(idx);
    setDeletingBulletId(null);
    setEditingTagBulletId(null);
    setShowColorPicker(null);
    setSearchQuery('');
    setIsSearching(false);
  }, []);

  // ─── Search across all days ───
  const searchResults = React.useMemo(() => {
    if (!searchQuery.trim()) return [];
    const q = searchQuery.toLowerCase();
    const results: { dateKey: string; displayDate: string; bullet: NoteBullet | null; freeform: FreeformSection | null }[] = [];

    for (const day of days) {
      const dDate = parseDate(day.Date);
      const displayDate = dDate.toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' });

      // Search bullets
      for (const b of day.Bullets || []) {
        const haystack = [b.Header, b.Content, ...(b.Tags || [])].join(' ').toLowerCase();
        if (haystack.includes(q)) {
          results.push({ dateKey: day.Date, displayDate, bullet: b, freeform: null });
        }
      }

      // Search freeform sections
      for (const s of day.FreeformSections || []) {
        if (s.Content && s.Content.toLowerCase().includes(q)) {
          results.push({ dateKey: day.Date, displayDate, bullet: null, freeform: s });
        }
      }
    }
    return results;
  }, [searchQuery, days]);

  // ─── Apply template ───
  const handleApplyTemplate = useCallback((template: typeof NOTE_TEMPLATES[0]) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    updateDays(prev => {
      const { days: updated, day, idx } = ensureDay(prev, selectedDateKey);
      const existingCount = day.Bullets.length;
      const newBullets = template.bullets.map((text, i) => {
        const b = createNoteBullet(text);
        b.Header = text;
        b.Content = '';
        b.SortOrder = existingCount + i;
        return b;
      });
      updated[idx] = { ...day, Bullets: [...day.Bullets, ...newBullets], LastModified: Date.now() };
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
          Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Heavy);
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
                  style={styles.bulletHeaderInput}
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
                style={styles.bulletContent}
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
                    <Text style={styles.tagPillText}>{tag}</Text>
                  </TouchableOpacity>
                ))}
                {isEditingTag ? (
                  <TextInput
                    style={styles.tagInput}
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
                    <Text style={styles.addTagText}>+ tag</Text>
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
    deletingBulletId, editingTagBulletId, newTagText, showColorPicker,
    updateBullet, handleTogglePin, handleAddTag, handleRemoveTag,
    handleToggleSubBullet, handleAddSubBullet, handleUpdateSubBulletText,
    handleDeleteBullet, handleSetColor,
  ]);

  // ═══════════════════════════════════════════════════════════
  // RENDER: Freeform Section Card
  // ═══════════════════════════════════════════════════════════

  const renderFreeformCard = useCallback(({ item, index }: { item: FreeformSection; index: number }) => (
    <View>
      <View style={styles.freeformCard}>
        <TextInput
          style={styles.freeformInput}
          value={item.Content}
          onChangeText={text => updateFreeformSection(item.Id, text)}
          placeholder="Start writing..."
          placeholderTextColor={colors.text.tertiary}
          multiline
        />
        <View style={styles.freeformMeta}>
          <Text style={styles.freeformTime}>
            {formatTime(item.CreatedAt)}
          </Text>
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
  ), [updateFreeformSection, handleAddFreeformSection]);

  // ═══════════════════════════════════════════════════════════
  // RENDER: Empty State
  // ═══════════════════════════════════════════════════════════

  const renderEmpty = () => (
    <View style={styles.emptyState}>
      <Text style={styles.emptyIcon}>{isFreeformMode ? '📝' : '✏️'}</Text>
      <Text style={styles.emptyTitle}>No notes for this day</Text>
      <Text style={styles.emptySubtitle}>
        Tap the + button to add your first{'\n'}
        {isFreeformMode ? 'freeform section' : 'bullet note'}
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
             (day.FreeformSections && day.FreeformSections.some(s => s.Content.trim())))
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
  // Sort pinned first, then by SortOrder
  const sortedBullets = [...bulletData].sort((a, b) => {
    if (a.IsPinned && !b.IsPinned) return -1;
    if (!a.IsPinned && b.IsPinned) return 1;
    return (a.SortOrder || 0) - (b.SortOrder || 0);
  });

  const freeformData = currentDay?.FreeformSections || [];

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
            <Text style={{ color: colors.text.tertiary, fontSize: 12, marginTop: 4 }}>Tap to add bullets to today's note</Text>
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
                        <Text style={{ color: colors.text.primary, fontSize: 14, fontWeight: '600' }} numberOfLines={1}>{r.bullet.Header}</Text>
                      ) : null}
                      {r.bullet.Content ? (
                        <Text style={{ color: colors.text.secondary, fontSize: 13, marginTop: 2 }} numberOfLines={2}>{r.bullet.Content}</Text>
                      ) : null}
                      {r.bullet.Tags && r.bullet.Tags.length > 0 ? (
                        <Text style={{ color: colors.accent.primary, fontSize: 11, marginTop: 4 }}>{r.bullet.Tags.join(', ')}</Text>
                      ) : null}
                    </>
                  ) : r.freeform ? (
                    <Text style={{ color: colors.text.secondary, fontSize: 13, fontStyle: 'italic' }} numberOfLines={3}>{r.freeform.Content}</Text>
                  ) : null}
                </TouchableOpacity>
              ))}
            </View>
          );
        })}
      </ScrollView>
    );
  };

  return (
    <View style={styles.container}>
      {renderTemplatesModal()}
      <LinearGradient
        colors={[colors.bg.base, colors.bg.baseEnd, colors.bg.base]}
        style={styles.gradient}
      >
        <SafeAreaView style={styles.gradient} edges={['top']}>
          {/* ─── Header ─── */}
          <View style={styles.header}>
            <Text style={styles.title}>Notes</Text>
            <View style={styles.headerRight}>
              {/* Sync indicator */}
              <View style={styles.syncIndicator}>
                <View style={[styles.syncDot, { backgroundColor: syncColor }]} />
                <Text style={[styles.syncText, { color: syncColor }]}>
                  {syncStatus === 'synced' ? 'SYNCED' : syncStatus === 'syncing' ? 'SYNC' : 'OFFLINE'}
                </Text>
              </View>

              {/* Templates button */}
              <TouchableOpacity
                style={{ padding: 6, marginRight: 2 }}
                onPress={() => setShowTemplates(true)}
                hitSlop={{ top: 8, bottom: 8, left: 8, right: 8 }}
                accessibilityLabel="Note templates"
                accessibilityRole="button"
              >
                <Text style={{ fontSize: 18 }}>📋</Text>
              </TouchableOpacity>

              {/* Export button */}
              <TouchableOpacity
                style={{ padding: 6, marginRight: 6 }}
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
                  style={[styles.modeButton, !isFreeformMode && styles.modeButtonActive]}
                  onPress={isFreeformMode ? handleToggleMode : undefined}
                  activeOpacity={isFreeformMode ? 0.7 : 1}
                  accessibilityLabel={`Bullet mode${!isFreeformMode ? ', active' : ''}`}
                  accessibilityRole="tab"
                >
                  <Text style={[
                    styles.modeButtonText,
                    !isFreeformMode && styles.modeButtonTextActive,
                  ]}>
                    Bullet
                  </Text>
                </TouchableOpacity>
                <TouchableOpacity
                  style={[styles.modeButton, isFreeformMode && styles.modeButtonActive]}
                  onPress={!isFreeformMode ? handleToggleMode : undefined}
                  activeOpacity={!isFreeformMode ? 0.7 : 1}
                  accessibilityLabel={`Free mode${isFreeformMode ? ', active' : ''}`}
                  accessibilityRole="tab"
                >
                  <Text style={[
                    styles.modeButtonText,
                    isFreeformMode && styles.modeButtonTextActive,
                  ]}>
                    Free
                  </Text>
                </TouchableOpacity>
              </View>
            </View>
          </View>

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
            {isSearching && searchQuery.trim() ? (
              renderSearchResults()
            ) : isFreeformMode ? (
              // Freeform mode
              freeformData.length === 0 ? (
                renderEmpty()
              ) : (
                <FlashListCast
                  data={freeformData}
                  renderItem={renderFreeformCard}
                  keyExtractor={(item: FreeformSection) => item.Id}
                  estimatedItemSize={160}
                  contentContainerStyle={styles.listContent}
                  showsVerticalScrollIndicator={false}
                />
              )
            ) : (
              // Bullet mode
              sortedBullets.length === 0 ? (
                renderEmpty()
              ) : (
                <FlashListCast
                  data={sortedBullets}
                  renderItem={renderBulletCard}
                  keyExtractor={(item: NoteBullet) => item.Id}
                  estimatedItemSize={200}
                  extraData={`${deletingBulletId}-${editingTagBulletId}-${showColorPicker}`}
                  contentContainerStyle={styles.listContent}
                  showsVerticalScrollIndicator={false}
                />
              )
            )}
          </View>

          {/* ─── FAB ─── */}
          <Animated.View style={[styles.fab, { transform: [{ scale: fabScale }] }]}>
            <TouchableOpacity
              onPress={isFreeformMode ? () => handleAddFreeformSection() : handleAddBullet}
              onPressIn={handleFabPressIn}
              onPressOut={handleFabPressOut}
              activeOpacity={0.9}
              style={{ width: '100%', height: '100%', justifyContent: 'center', alignItems: 'center' }}
              accessibilityLabel={isFreeformMode ? 'Add freeform section' : 'Add bullet note'}
              accessibilityRole="button"
            >
              <Text style={styles.fabText}>+</Text>
            </TouchableOpacity>
          </Animated.View>
        </SafeAreaView>
      </LinearGradient>
    </View>
  );
}
