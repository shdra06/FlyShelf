import React, { useState, useMemo, useEffect, useCallback } from 'react';
import { View, Text, StyleSheet, TouchableOpacity, ScrollView, Platform } from 'react-native';
import Animated, { FadeInDown, FadeIn } from 'react-native-reanimated';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import ColorPicker, { Panel1, HueSlider, OpacitySlider, returnedResults } from 'reanimated-color-picker';

import { useAppTheme } from '../../hooks/useAppTheme';
import { font, space, radius } from '../../styles/theme';

interface ColorPickerToolProps {
  onBack: () => void;
}

const SAVED_COLORS_KEY = '@color_picker_saved';

const PRESET_COLORS = [
  '#F44336', '#E91E63', '#9C27B0', '#673AB7',
  '#3F51B5', '#2196F3', '#03A9F4', '#00BCD4',
  '#009688', '#4CAF50', '#8BC34A', '#CDDC39',
  '#FFEB3B', '#FFC107', '#FF9800', '#FF5722'
];

export default function ColorPickerTool({ onBack }: ColorPickerToolProps) {
  const { colors, shadows } = useAppTheme();
  const insets = useSafeAreaInsets();
  
  const [currentColor, setCurrentColor] = useState('#4F6BFF');
  const [colorFormats, setColorFormats] = useState({
    hex: '#4F6BFF',
    rgb: 'rgb(79, 107, 255)',
    hsl: 'hsl(230, 100%, 65%)'
  });
  
  const [savedColors, setSavedColors] = useState<string[]>([]);

  useEffect(() => {
    const loadSavedColors = async () => {
      try {
        const stored = await AsyncStorage.getItem(SAVED_COLORS_KEY);
        if (stored) {
          setSavedColors(JSON.parse(stored));
        }
      } catch (e) {}
    };
    loadSavedColors();
  }, []);

  const saveCurrentColor = async () => {
    Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    if (!savedColors.includes(colorFormats.hex)) {
      const newSaved = [colorFormats.hex, ...savedColors].slice(0, 20);
      setSavedColors(newSaved);
      try {
        await AsyncStorage.setItem(SAVED_COLORS_KEY, JSON.stringify(newSaved));
      } catch (e) {}
    }
  };

  const clearSavedColors = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setSavedColors([]);
    try {
      await AsyncStorage.removeItem(SAVED_COLORS_KEY);
    } catch (e) {}
  };

  const onColorSelect = (results: returnedResults) => {
    setCurrentColor(results.hex);
    setColorFormats({
      hex: results.hex,
      rgb: results.rgb,
      hsl: results.hsl,
    });
  };

  const handleCopy = (text: string, format: string) => {
    Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    // In a real app, use Clipboard.setStringAsync from expo-clipboard here
    console.log(`Copied ${format}: ${text}`);
  };

  const styles = useMemo(() => StyleSheet.create({
    container: {
      flex: 1,
      backgroundColor: colors.bg.base,
      paddingTop: insets.top,
    },
    header: {
      flexDirection: 'row',
      alignItems: 'center',
      paddingHorizontal: space.lg,
      paddingVertical: space.md,
      borderBottomWidth: 1,
      borderBottomColor: colors.border.subtle,
    },
    backButton: {
      padding: space.sm,
      marginRight: space.sm,
    },
    title: {
      fontFamily: font.semibold,
      fontSize: 18,
      color: colors.text.primary,
    },
    content: {
      flex: 1,
    },
    previewSection: {
      alignItems: 'center',
      paddingVertical: space.xl,
    },
    colorPreviewCircle: {
      width: 80,
      height: 80,
      borderRadius: 40,
      borderWidth: 4,
      borderColor: colors.bg.card,
      ...shadows.elevated,
    },
    pickerContainer: {
      paddingHorizontal: space.lg,
      marginBottom: space.xl,
      gap: space.lg,
    },
    formatsContainer: {
      paddingHorizontal: space.lg,
      marginBottom: space.xl,
      gap: space.sm,
    },
    formatRow: {
      flexDirection: 'row',
      alignItems: 'center',
      backgroundColor: colors.bg.card,
      padding: space.md,
      borderRadius: radius.md,
      borderWidth: 1,
      borderColor: colors.border.subtle,
    },
    formatLabel: {
      fontFamily: font.bold,
      fontSize: 12,
      color: colors.text.tertiary,
      width: 40,
    },
    formatValue: {
      flex: 1,
      fontFamily: font.medium,
      fontSize: 14,
      color: colors.text.primary,
    },
    copyButton: {
      padding: space.xs,
    },
    sectionTitleRow: {
      flexDirection: 'row',
      justifyContent: 'space-between',
      alignItems: 'center',
      paddingHorizontal: space.lg,
      marginBottom: space.md,
    },
    sectionTitle: {
      fontFamily: font.semibold,
      fontSize: 16,
      color: colors.text.primary,
    },
    clearButtonText: {
      fontFamily: font.medium,
      fontSize: 12,
      color: colors.accent.error,
    },
    paletteScroll: {
      paddingHorizontal: space.lg,
      paddingBottom: space.xl,
      gap: space.sm,
    },
    swatch: {
      width: 40,
      height: 40,
      borderRadius: radius.pill,
      borderWidth: 2,
      borderColor: colors.border.subtle,
    },
    saveButton: {
      marginHorizontal: space.lg,
      backgroundColor: colors.accent.primary,
      paddingVertical: space.md,
      borderRadius: radius.md,
      alignItems: 'center',
      marginBottom: space.xl,
    },
    saveButtonText: {
      fontFamily: font.semibold,
      fontSize: 16,
      color: '#FFFFFF',
    }
  }), [colors, insets.top, shadows.elevated]);

  return (
    <View style={styles.container}>
      {/* Header */}
      <View style={styles.header}>
        <TouchableOpacity style={styles.backButton} onPress={onBack}>
          <Ionicons name="arrow-back" size={24} color={colors.text.primary} />
        </TouchableOpacity>
        <Text style={styles.title}>Color Picker</Text>
      </View>

      <ScrollView style={styles.content} showsVerticalScrollIndicator={false}>
        {/* Preview */}
        <Animated.View style={styles.previewSection} entering={FadeInDown.duration(400)}>
          <View style={[styles.colorPreviewCircle, { backgroundColor: currentColor }]} />
        </Animated.View>

        {/* Picker */}
        <Animated.View style={styles.pickerContainer} entering={FadeInDown.delay(100).duration(400)}>
          <ColorPicker 
            style={{ width: '100%', gap: space.lg }} 
            value={currentColor} 
            onComplete={onColorSelect}
            boundedThumb
          >
            <Panel1 style={{ height: 200, borderRadius: radius.lg }} />
            <HueSlider style={{ height: 24, borderRadius: radius.pill }} />
            <OpacitySlider style={{ height: 24, borderRadius: radius.pill }} />
          </ColorPicker>
        </Animated.View>

        {/* Formats */}
        <Animated.View style={styles.formatsContainer} entering={FadeInDown.delay(200).duration(400)}>
          {(Object.keys(colorFormats) as Array<keyof typeof colorFormats>).map((fmt) => (
            <View key={fmt} style={styles.formatRow}>
              <Text style={styles.formatLabel}>{fmt.toUpperCase()}</Text>
              <Text style={styles.formatValue}>{colorFormats[fmt]}</Text>
              <TouchableOpacity style={styles.copyButton} onPress={() => handleCopy(colorFormats[fmt], fmt)}>
                <Ionicons name="copy-outline" size={20} color={colors.text.secondary} />
              </TouchableOpacity>
            </View>
          ))}
        </Animated.View>

        {/* Save Button */}
        <Animated.View entering={FadeInDown.delay(300).duration(400)}>
          <TouchableOpacity style={styles.saveButton} onPress={saveCurrentColor} activeOpacity={0.8}>
            <Text style={styles.saveButtonText}>Save Color</Text>
          </TouchableOpacity>
        </Animated.View>

        {/* Presets */}
        <Animated.View entering={FadeIn.delay(400)}>
          <View style={styles.sectionTitleRow}>
            <Text style={styles.sectionTitle}>Presets</Text>
          </View>
          <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.paletteScroll}>
            {PRESET_COLORS.map((c) => (
              <TouchableOpacity 
                key={c} 
                style={[styles.swatch, { backgroundColor: c }]} 
                onPress={() => {
                  Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
                  setCurrentColor(c);
                  setColorFormats({ hex: c, rgb: '', hsl: '' }); // Simplified, in real app need proper conversion
                }}
              />
            ))}
          </ScrollView>
        </Animated.View>

        {/* Saved Colors */}
        {savedColors.length > 0 && (
          <Animated.View entering={FadeIn.delay(500)}>
            <View style={styles.sectionTitleRow}>
              <Text style={styles.sectionTitle}>Saved Colors</Text>
              <TouchableOpacity onPress={clearSavedColors}>
                <Text style={styles.clearButtonText}>Clear All</Text>
              </TouchableOpacity>
            </View>
            <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.paletteScroll}>
              {savedColors.map((c, i) => (
                <TouchableOpacity 
                  key={`${c}-${i}`} 
                  style={[styles.swatch, { backgroundColor: c }]} 
                  onPress={() => {
                    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
                    setCurrentColor(c);
                    setColorFormats({ hex: c, rgb: '', hsl: '' });
                  }}
                />
              ))}
            </ScrollView>
          </Animated.View>
        )}
      </ScrollView>
    </View>
  );
}
