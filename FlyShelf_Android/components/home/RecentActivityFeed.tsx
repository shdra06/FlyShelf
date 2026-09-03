import React from 'react';
import { View, Text, FlatList, Pressable } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useAppTheme } from '../../hooks/useAppTheme';
import createHomeStyles from '../../styles/homeStyles';

export interface ActivityItem {
  id: string;
  type: 'clipboard' | 'file' | 'note' | 'task';
  title: string;
  timestamp: number;
  icon: string;
  iconColor: string;
}

interface RecentActivityFeedProps {
  items: ActivityItem[];
  onItemPress?: (item: ActivityItem) => void;
  onSeeAll?: () => void;
  maxItems?: number;
}

function getRelativeTime(timestamp: number): string {
  const now = Date.now();
  const diff = now - timestamp;
  const minutes = Math.floor(diff / 60000);
  const hours = Math.floor(minutes / 60);
  const days = Math.floor(hours / 24);

  if (minutes < 1) return 'Just now';
  if (minutes < 60) return `${minutes}m ago`;
  if (hours < 24) return `${hours}h ago`;
  if (days === 1) return 'Yesterday';
  return `${days}d ago`;
}

export default function RecentActivityFeed({
  items,
  onItemPress,
  onSeeAll,
  maxItems = 10,
}: RecentActivityFeedProps) {
  const { colors, shadows } = useAppTheme();
  const styles = createHomeStyles(colors, shadows);

  const displayItems = items.slice(0, maxItems);
  const showSeeAll = items.length > 3;

  const renderItem = ({ item }: { item: ActivityItem }) => (
    <Pressable 
      style={({ pressed }) => [
        styles.activityItem,
        pressed && { backgroundColor: colors.bg.cardHover }
      ]}
      onPress={() => {
        try { onItemPress?.(item); } catch (e) { console.error('Activity item press error:', e); }
      }}
    >
      <View style={[styles.activityIconWrap, { backgroundColor: `${item.iconColor}18` }]}>
        <Ionicons name={item.icon as any} size={20} color={item.iconColor} />
      </View>
      <View style={styles.activityContent}>
        <Text style={styles.activityTitle} numberOfLines={1}>{item.title}</Text>
        <Text style={styles.activityTime}>{getRelativeTime(item.timestamp)}</Text>
      </View>
    </Pressable>
  );

  return (
    <View>
      <View style={styles.sectionHeader}>
        <Text style={styles.sectionTitle}>Recent Activity</Text>
        {showSeeAll && (
          <Pressable
            onPress={() => {
              try { onSeeAll?.(); } catch (e) { console.error('See all press error:', e); }
            }}
            hitSlop={8}
            accessibilityLabel="See all recent activity"
            accessibilityRole="button"
          >
            <Text style={styles.sectionSeeAll}>See All</Text>
          </Pressable>
        )}
      </View>

      {displayItems.length > 0 ? (
        <FlatList
          data={displayItems}
          keyExtractor={item => item.id}
          renderItem={renderItem}
          scrollEnabled={false}
          ItemSeparatorComponent={() => <View style={styles.activityDivider} />}
        />
      ) : (
        <View style={styles.emptyState}>
          <Ionicons name="time-outline" size={48} color={colors.text.tertiary} style={styles.emptyIcon} />
          <Text style={styles.emptyTitle}>No recent activity</Text>
          <Text style={styles.emptySubtitle}>Items you sync or create will appear here.</Text>
        </View>
      )}
    </View>
  );
}
