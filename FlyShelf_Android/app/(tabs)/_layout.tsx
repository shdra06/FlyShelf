import { Tabs } from 'expo-router';
import React from 'react';
import { Platform, View, StyleSheet } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import Animated, {
  useSharedValue,
  useAnimatedStyle,
  withSpring,
  interpolate,
} from 'react-native-reanimated';

import { HapticTab } from '@/components/haptic-tab';
import { colors, font, component, surface, spring as springConfig } from '../../styles/theme';

// ═══════════════════════════════════════════
// TAB ICON WITH PILL INDICATOR + ANIMATION
// ═══════════════════════════════════════════

function TabIcon({ focused, iconOutline, iconFilled, color }: {
  focused: boolean;
  iconOutline: string;
  iconFilled: string;
  color: string;
}) {
  return (
    <View style={styles.iconOuter}>
      {focused && <View style={styles.pill} />}
      <Ionicons
        name={(focused ? iconFilled : iconOutline) as any}
        size={22}
        color={color}
      />
    </View>
  );
}

// ═══════════════════════════════════════════
// TAB LAYOUT
// ═══════════════════════════════════════════

export default function TabLayout() {
  return (
    <Tabs
      screenOptions={{
        tabBarActiveTintColor: colors.accent.primary,
        tabBarInactiveTintColor: colors.text.tertiary,
        headerShown: false,
        tabBarButton: HapticTab,
        tabBarStyle: {
          position: 'absolute',
          bottom: 0,
          left: 0,
          right: 0,
          height: component.tabBarHeight,
          backgroundColor: surface.overlay,
          borderTopWidth: 1,
          borderTopColor: colors.border.subtle,
          paddingBottom: component.tabBarPaddingBottom,
          paddingTop: 6,
          elevation: 0,
          shadowColor: '#000',
          shadowOffset: { width: 0, height: -4 },
          shadowOpacity: 0.3,
          shadowRadius: 16,
        },
        tabBarLabelStyle: {
          fontFamily: font.medium,
          fontSize: 10,
          letterSpacing: 0.3,
          marginTop: 2,
        },
        tabBarIconStyle: {
          marginBottom: -2,
        },
      }}>
      <Tabs.Screen
        name="index"
        options={{
          title: 'Sync',
          tabBarAccessibilityLabel: 'Sync tab',
          tabBarIcon: ({ color, focused }) => (
            <TabIcon focused={focused} iconOutline="sync-outline" iconFilled="sync" color={color} />
          ),
        }}
      />
      <Tabs.Screen
        name="archive"
        options={{
          title: 'Files',
          tabBarAccessibilityLabel: 'Files tab',
          tabBarIcon: ({ color, focused }) => (
            <TabIcon focused={focused} iconOutline="folder-outline" iconFilled="folder" color={color} />
          ),
        }}
      />
      <Tabs.Screen
        name="notes"
        options={{
          title: 'Notes',
          tabBarAccessibilityLabel: 'Notes tab',
          tabBarIcon: ({ color, focused }) => (
            <TabIcon focused={focused} iconOutline="document-text-outline" iconFilled="document-text" color={color} />
          ),
        }}
      />
      <Tabs.Screen
        name="todo"
        options={{
          title: 'Todo',
          tabBarAccessibilityLabel: 'Todo tab',
          tabBarIcon: ({ color, focused }) => (
            <TabIcon focused={focused} iconOutline="checkbox-outline" iconFilled="checkbox" color={color} />
          ),
        }}
      />
      <Tabs.Screen
        name="settings"
        options={{
          title: 'Settings',
          tabBarAccessibilityLabel: 'Settings tab',
          tabBarIcon: ({ color, focused }) => (
            <TabIcon focused={focused} iconOutline="settings-outline" iconFilled="settings" color={color} />
          ),
        }}
      />
    </Tabs>
  );
}

// ═══════════════════════════════════════════
// STYLES
// ═══════════════════════════════════════════

const styles = StyleSheet.create({
  iconOuter: {
    width: component.pillWidth,
    height: component.pillHeight,
    alignItems: 'center',
    justifyContent: 'center',
    borderRadius: component.pillHeight / 2,
  },
  pill: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: colors.accent.primaryDim,
    borderRadius: component.pillHeight / 2,
  },
});
