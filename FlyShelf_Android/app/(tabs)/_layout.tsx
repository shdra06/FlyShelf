import { Tabs } from 'expo-router';
import React, { useMemo, useEffect } from 'react';
import { View, StyleSheet, ViewStyle, Platform } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Notifications from 'expo-notifications';

import { HapticTab } from '@/components/haptic-tab';
import { useAppTheme } from '@/hooks/useAppTheme';
import { font, component } from '../../styles/theme';

// ═══════════════════════════════════════════
// TAB ICON WITH PILL INDICATOR
// ═══════════════════════════════════════════

function TabIcon({ focused, iconOutline, iconFilled, color, pillColor }: {
  focused: boolean;
  iconOutline: string;
  iconFilled: string;
  color: string;
  pillColor: string;
}) {
  return (
    <View style={iconStyles.iconOuter}>
      {focused && <View style={[iconStyles.pill, { backgroundColor: pillColor }]} />}
      <Ionicons
        name={(focused ? iconFilled : iconOutline) as any}
        size={22}
        color={color}
      />
    </View>
  );
}

const iconStyles = StyleSheet.create({
  iconOuter: {
    width: component.pillWidth,
    height: component.pillHeight,
    alignItems: 'center',
    justifyContent: 'center',
    borderRadius: component.pillHeight / 2,
  },
  pill: {
    ...StyleSheet.absoluteFillObject,
    borderRadius: component.pillHeight / 2,
  },
});

// ═══════════════════════════════════════════
// TAB LAYOUT — 5 Tabs (Home, Files, Notes, Tasks, Vault)
// Settings moved to Home header
// ═══════════════════════════════════════════

export default function TabLayout() {
  const { colors, surface } = useAppTheme();

  useEffect(() => {
    if (Platform.OS === 'android') {
      Notifications.setNotificationChannelAsync('sync_clips', {
        name: 'Clip Sync',
        importance: Notifications.AndroidImportance.DEFAULT,
      });
      Notifications.setNotificationChannelAsync('sync_files', {
        name: 'File Sync',
        importance: Notifications.AndroidImportance.HIGH,
      });
      Notifications.setNotificationChannelAsync('sync_status', {
        name: 'Sync Status',
        importance: Notifications.AndroidImportance.LOW,
      });
    }
  }, []);

  const tabBarStyle: ViewStyle = useMemo(() => ({
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
    elevation: 8,
    shadowColor: '#000',
    shadowOffset: { width: 0, height: -4 },
    shadowOpacity: 0.3,
    shadowRadius: 16,
  }), [colors, surface]);

  return (
    <Tabs
      screenOptions={{
        tabBarActiveTintColor: colors.accent.primary,
        tabBarInactiveTintColor: colors.text.tertiary,
        headerShown: false,
        tabBarButton: HapticTab,
        tabBarStyle,
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
          title: 'Home',
          tabBarAccessibilityLabel: 'Home tab',
          tabBarIcon: ({ color, focused }) => (
            <TabIcon focused={focused} iconOutline="home-outline" iconFilled="home" color={color} pillColor={colors.accent.primaryDim} />
          ),
        }}
      />
      <Tabs.Screen
        name="archive"
        options={{
          title: 'Files',
          tabBarAccessibilityLabel: 'Files tab',
          tabBarIcon: ({ color, focused }) => (
            <TabIcon focused={focused} iconOutline="folder-outline" iconFilled="folder" color={color} pillColor={colors.accent.primaryDim} />
          ),
        }}
      />
      <Tabs.Screen
        name="notes"
        options={{
          title: 'Notes',
          tabBarAccessibilityLabel: 'Notes tab',
          tabBarIcon: ({ color, focused }) => (
            <TabIcon focused={focused} iconOutline="document-text-outline" iconFilled="document-text" color={color} pillColor={colors.accent.primaryDim} />
          ),
        }}
      />
      <Tabs.Screen
        name="todo"
        options={{
          title: 'Tasks',
          tabBarAccessibilityLabel: 'Tasks tab',
          tabBarIcon: ({ color, focused }) => (
            <TabIcon focused={focused} iconOutline="checkbox-outline" iconFilled="checkbox" color={color} pillColor={colors.accent.primaryDim} />
          ),
        }}
      />
      <Tabs.Screen
        name="vault"
        options={{
          title: 'Vault',
          tabBarAccessibilityLabel: 'Vault tab',
          tabBarIcon: ({ color, focused }) => (
            <TabIcon focused={focused} iconOutline="lock-closed-outline" iconFilled="lock-closed" color={color} pillColor={colors.accent.primaryDim} />
          ),
        }}
      />
      {/* Settings is hidden from tabs — accessible via Home header gear icon */}
      <Tabs.Screen
        name="settings"
        options={{
          href: null, // Hidden from tab bar
          title: 'Settings',
        }}
      />
    </Tabs>
  );
}
