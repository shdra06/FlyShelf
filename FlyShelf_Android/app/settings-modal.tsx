// Settings Modal — wrapper route for settings accessed from Home header
import React from 'react';
import { View, TouchableOpacity, StyleSheet } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { router } from 'expo-router';
import { useAppTheme } from '../hooks/useAppTheme';

// Re-export the settings screen directly (it's already a full component)
import SettingsScreen from './(tabs)/settings';

export default function SettingsModal() {
  const { colors } = useAppTheme();

  return (
    <View style={{ flex: 1, backgroundColor: colors.bg.base }}>
      {/* Close bar with drag handle and close button */}
      <View style={{
        flexDirection: 'row',
        alignItems: 'center',
        justifyContent: 'space-between',
        paddingHorizontal: 16,
        paddingTop: 12,
        paddingBottom: 4,
      }}>
        <View style={{ width: 36 }} />
        <View style={{
          width: 40,
          height: 4,
          borderRadius: 2,
          backgroundColor: colors.border.medium,
        }} />
        <TouchableOpacity
          onPress={() => router.back()}
          style={{
            width: 36,
            height: 36,
            borderRadius: 18,
            backgroundColor: colors.bg.cardHover,
            alignItems: 'center',
            justifyContent: 'center',
          }}
          accessibilityLabel="Close settings"
          accessibilityRole="button"
        >
          <Ionicons name="close" size={20} color={colors.text.secondary} />
        </TouchableOpacity>
      </View>
      <SettingsScreen />
    </View>
  );
}
