import { Tabs } from 'expo-router';
import React from 'react';
import { Platform, View, StyleSheet } from 'react-native';
import { BlurView } from 'expo-blur';

import { HapticTab } from '@/components/haptic-tab';
import { IconSymbol } from '@/components/ui/icon-symbol';
import { colors, font } from '../../styles/theme';

export default function TabLayout() {
  return (
    <Tabs
      screenOptions={{
        tabBarActiveTintColor: '#6384FF',
        tabBarInactiveTintColor: '#555C6B',
        headerShown: false,
        tabBarButton: HapticTab,
        tabBarStyle: {
          position: 'absolute',
          bottom: 0,
          left: 0,
          right: 0,
          height: Platform.OS === 'ios' ? 88 : 64,
          backgroundColor: 'rgba(11, 13, 18, 0.92)',
          borderTopWidth: 1,
          borderTopColor: 'rgba(255,255,255,0.06)',
          paddingBottom: Platform.OS === 'ios' ? 24 : 8,
          paddingTop: 8,
          elevation: 0,
          shadowColor: '#000',
          shadowOffset: { width: 0, height: -4 },
          shadowOpacity: 0.3,
          shadowRadius: 12,
        },
        tabBarLabelStyle: {
          fontFamily: font.semibold,
          fontSize: 10,
          letterSpacing: 0.3,
          marginTop: 2,
        },
        tabBarIconStyle: {
          marginBottom: -2,
        },
        // Frosted glass effect on iOS
        ...(Platform.OS === 'ios' ? {
          tabBarBackground: () => (
            <BlurView intensity={80} tint="dark" style={StyleSheet.absoluteFill} />
          ),
        } : {}),
      }}>
      <Tabs.Screen
        name="index"
        options={{
          title: 'Sync',
          tabBarIcon: ({ color, focused }) => (
            <View style={focused ? styles.activeIconContainer : undefined}>
              <IconSymbol size={24} name="repeat" color={color} />
            </View>
          ),
        }}
      />
      <Tabs.Screen
        name="archive"
        options={{
          title: 'Files',
          tabBarIcon: ({ color, focused }) => (
            <View style={focused ? styles.activeIconContainer : undefined}>
              <IconSymbol size={24} name="folder" color={color} />
            </View>
          ),
        }}
      />
      <Tabs.Screen
        name="settings"
        options={{
          title: 'Settings',
          tabBarIcon: ({ color, focused }) => (
            <View style={focused ? styles.activeIconContainer : undefined}>
              <IconSymbol size={24} name="gear" color={color} />
            </View>
          ),
        }}
      />
    </Tabs>
  );
}

const styles = StyleSheet.create({
  activeIconContainer: {
    backgroundColor: 'rgba(99, 132, 255, 0.12)',
    borderRadius: 12,
    padding: 6,
    marginBottom: -4,
  },
});
