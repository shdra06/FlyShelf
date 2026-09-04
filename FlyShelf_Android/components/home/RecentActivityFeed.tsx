import React, { useState, useMemo } from 'react';
import { View, Text, FlatList, Pressable, StyleSheet, ScrollView } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import { useAppTheme } from '../../hooks/useAppTheme';
import { font, radius } from '../../styles/theme';
import createHomeStyles from '../../styles/homeStyles';

export type ActivityType = 'clipboard' | 'file' | 'note' | 'task' | 'vault';

export interface ActivityItem {
  id: string;
  type: ActivityType;
  title: string;
  subtitle?: string;
  timestamp: number;
  icon: string;
  iconColor: string;
  rawPayload?: any;
}

interface RecentActivityFeedProps {
  items: ActivityItem[];
  onItemPress?: (item: ActivityItem) => void;
  onSeeAll?: () => void;
  maxItems?: number;
}

function getRelativeTime(timestamp: number): string {
  const now = Date.now();
  const diff = Math.max(0, now - timestamp);
  const minutes = Math.floor(diff / 60000);
  const hours = Math.floor(minutes / 60);
  const days = Math.floor(hours / 24);

  if (minutes < 1) return 'Just now';
  if (minutes < 60) return `${minutes}m ago`;
  if (hours < 24) return `${hours}h ago`;
  if (days === 1) return 'Yesterday';
  if (days < 7) return `${days}d ago`;
  return new Date(timestamp).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}

export default function RecentActivityFeed({
  items,
  onItemPress,
  onSeeAll,
  maxItems = 15,
}: RecentActivityFeedProps) {
  const { colors, shadows } = useAppTheme();
  const styles = createHomeStyles(colors, shadows);
  const [activeFilter, setActiveFilter] = useState<'all' | ActivityType>('all');

  const filteredItems = useMemo(() => {
    if (activeFilter === 'all') return items.slice(0, maxItems);
    return items.filter(item => item.type === activeFilter).slice(0, maxItems);
  }, [items, activeFilter, maxItems]);

  const showSeeAll = items.length > 4;

  const filterTabs: { key: 'all' | ActivityType; label: string; icon: string }[] = [
    { key: 'all', label: 'All', icon: 'sparkles' },
    { key: 'clipboard', label: 'Clips', icon: 'clipboard-outline' },
    { key: 'note', label: 'Notes', icon: 'create-outline' },
    { key: 'task', label: 'Tasks', icon: 'checkbox-outline' },
    { key: 'vault', label: 'Storage', icon: 'cube-outline' },
  ];

  const renderItem = ({ item }: { item: ActivityItem }) => (
    <Pressable 
      style={({ pressed }) => [
        styles.activityItem,
        pressed && { backgroundColor: colors.bg.cardHover },
        { paddingVertical: 12 }
      ]}
      onPress={() => {
        try {
          Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
          onItemPress?.(item);
        } catch (e) {
          console.error('Activity item press error:', e);
        }
      }}
    >
      <View style={[styles.activityIconWrap, { backgroundColor: `${item.iconColor}16` }]}>
        <Ionicons name={item.icon as any} size={18} color={item.iconColor} />
      </View>
      <View style={styles.activityContent}>
        <Text style={styles.activityTitle} numberOfLines={1}>{item.title}</Text>
        <View style={customStyles.metaRow}>
          {item.subtitle ? (
            <>
              <Text style={[customStyles.subTag, { color: item.iconColor }]}>
                {item.subtitle}
              </Text>
              <Text style={customStyles.dot}>•</Text>
            </>
          ) : null}
          <Text style={styles.activityTime}>{getRelativeTime(item.timestamp)}</Text>
        </View>
      </View>
      <Ionicons name="chevron-forward" size={14} color={colors.text.tertiary} style={{ opacity: 0.5 }} />
    </Pressable>
  );

  return (
    <View style={{ marginBottom: 24 }}>
      {/* Section Header */}
      <View style={styles.sectionHeader}>
        <View style={{ flexDirection: 'row', alignItems: 'center', gap: 6 }}>
          <Ionicons name="time-outline" size={18} color={colors.accent.primary} />
          <Text style={styles.sectionTitle}>Smart Recent Activity</Text>
        </View>
        {showSeeAll && (
          <Pressable
            onPress={() => {
              try { onSeeAll?.(); } catch (e) { console.error('See all press error:', e); }
            }}
            hitSlop={8}
            accessibilityLabel="See all recent activity"
            accessibilityRole="button"
          >
            <Text style={styles.sectionSeeAll}>View All</Text>
          </Pressable>
        )}
      </View>

      {/* Filter Chips Row */}
      <ScrollView
        horizontal
        showsHorizontalScrollIndicator={false}
        contentContainerStyle={customStyles.filterRow}
      >
        {filterTabs.map(tab => {
          const isSelected = activeFilter === tab.key;
          return (
            <Pressable
              key={tab.key}
              style={[
                customStyles.filterChip,
                { backgroundColor: isSelected ? colors.accent.primary : colors.bg.input },
                { borderColor: isSelected ? colors.accent.primary : colors.border.subtle }
              ]}
              onPress={() => {
                Haptics.selectionAsync();
                setActiveFilter(tab.key);
              }}
            >
              <Ionicons
                name={tab.icon as any}
                size={12}
                color={isSelected ? '#FFF' : colors.text.secondary}
              />
              <Text
                style={[
                  customStyles.filterText,
                  { color: isSelected ? '#FFF' : colors.text.secondary },
                  isSelected && { fontFamily: font.bold }
                ]}
              >
                {tab.label}
              </Text>
            </Pressable>
          );
        })}
      </ScrollView>

      {/* Activity List */}
      {filteredItems.length > 0 ? (
        <FlatList
          data={filteredItems}
          keyExtractor={item => item.id}
          renderItem={renderItem}
          scrollEnabled={false}
          ItemSeparatorComponent={() => <View style={styles.activityDivider} />}
        />
      ) : (
        <View style={styles.emptyState}>
          <Ionicons name="time-outline" size={40} color={colors.text.tertiary} style={styles.emptyIcon} />
          <Text style={styles.emptyTitle}>
            {activeFilter === 'all' ? 'No recent activity' : `No ${activeFilter} activity`}
          </Text>
          <Text style={styles.emptySubtitle}>
            {activeFilter === 'all'
              ? 'Interactions across clips, notes, tasks, and vault will appear here.'
              : `Create or sync ${activeFilter} items to see them in this smart timeline.`}
          </Text>
        </View>
      )}
    </View>
  );
}

const customStyles = StyleSheet.create({
  filterRow: {
    paddingHorizontal: 20,
    gap: 8,
    paddingBottom: 12,
  },
  filterChip: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 5,
    paddingHorizontal: 12,
    paddingVertical: 6,
    borderRadius: 16,
    borderWidth: 1,
  },
  filterText: {
    fontSize: 12,
    fontFamily: font.medium,
  },
  metaRow: {
    flexDirection: 'row',
    alignItems: 'center',
    marginTop: 2,
    gap: 6,
  },
  subTag: {
    fontSize: 11,
    fontFamily: font.semibold,
  },
  dot: {
    fontSize: 10,
    color: '#9CA3AF',
  },
});
