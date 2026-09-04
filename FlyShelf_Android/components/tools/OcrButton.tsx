// OcrButton — Reusable OCR text extraction button for any image
// Shows an "Extract Text" button. When pressed, runs ML Kit OCR on the image
// and displays the extracted text in a modal with copy functionality.
import React, { useState, useCallback, useMemo } from 'react';
import {
  View, Text, Pressable, Modal, ScrollView, ActivityIndicator,
  StyleSheet, Alert,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as Clipboard from 'expo-clipboard';
import Animated, { FadeInDown } from 'react-native-reanimated';
import { useAppTheme } from '../../hooks/useAppTheme';
import { font, space, radius } from '../../styles/theme';

interface OcrButtonProps {
  /** URI of the image to extract text from */
  imageUri: string;
  /** Button style variant */
  variant?: 'icon' | 'chip' | 'full';
  /** Optional label override */
  label?: string;
}

/**
 * Extracts text from an image using expo-text-extractor (ML Kit / Vision).
 * Falls back gracefully if the package is not available.
 */
async function extractText(uri: string): Promise<string> {
  try {
    const { extractTextFromImage } = require('@zhanziyang/expo-text-extractor');
    const results: string[] = await extractTextFromImage(uri);
    if (!results || results.length === 0) return '';
    return results.join('\n');
  } catch (e: any) {
    // Fallback: try alternate package names
    try {
      const mod = require('expo-text-extractor');
      const results = await mod.extractTextFromImage(uri);
      return Array.isArray(results) ? results.join('\n') : String(results || '');
    } catch {
      throw new Error(e?.message || 'Text extraction is not available on this device');
    }
  }
}

export default function OcrButton({ imageUri, variant = 'chip', label }: OcrButtonProps) {
  const { colors } = useAppTheme();
  const [loading, setLoading] = useState(false);
  const [resultText, setResultText] = useState<string | null>(null);
  const [modalVisible, setModalVisible] = useState(false);
  const [copied, setCopied] = useState(false);

  const handleExtract = useCallback(async () => {
    if (!imageUri) return;
    setLoading(true);
    setCopied(false);
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);

    try {
      const text = await extractText(imageUri);
      setLoading(false);
      if (!text.trim()) {
        Haptics.notificationAsync(Haptics.NotificationFeedbackType.Warning);
        Alert.alert('No Text Found', 'Could not detect any text in this image. Try with a clearer image or document.');
        return;
      }
      setResultText(text);
      setModalVisible(true);
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    } catch (e: any) {
      setLoading(false);
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Error);
      Alert.alert('OCR Error', e.message || 'Failed to extract text from image');
    }
  }, [imageUri]);

  const handleCopy = useCallback(async () => {
    if (!resultText) return;
    await Clipboard.setStringAsync(resultText);
    setCopied(true);
    Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    setTimeout(() => setCopied(false), 2000);
  }, [resultText]);

  const handleClose = useCallback(() => {
    setModalVisible(false);
  }, []);

  const s = useMemo(() => StyleSheet.create({
    // Icon variant
    iconBtn: {
      width: 40, height: 40, borderRadius: 12,
      backgroundColor: `${colors.accent.info}18`,
      alignItems: 'center', justifyContent: 'center',
    },
    // Chip variant
    chipBtn: {
      flexDirection: 'row', alignItems: 'center', gap: 6,
      paddingHorizontal: 14, paddingVertical: 8,
      borderRadius: radius.pill,
      backgroundColor: `${colors.accent.info}16`,
      borderWidth: 1, borderColor: `${colors.accent.info}30`,
    },
    chipLabel: {
      fontFamily: font.semibold, fontSize: 12, color: colors.accent.info,
    },
    // Full variant
    fullBtn: {
      flexDirection: 'row', alignItems: 'center', justifyContent: 'center',
      gap: 8, paddingVertical: 12, paddingHorizontal: 20,
      borderRadius: radius.md,
      backgroundColor: `${colors.accent.info}16`,
      borderWidth: 1, borderColor: `${colors.accent.info}30`,
    },
    fullLabel: {
      fontFamily: font.semibold, fontSize: 14, color: colors.accent.info,
    },
    // Modal
    overlay: {
      flex: 1, backgroundColor: 'rgba(0,0,0,0.6)',
      justifyContent: 'flex-end',
    },
    sheet: {
      backgroundColor: colors.bg.elevated,
      borderTopLeftRadius: 24, borderTopRightRadius: 24,
      maxHeight: '70%', paddingBottom: 40,
    },
    handle: {
      width: 36, height: 4, borderRadius: 2,
      backgroundColor: colors.border.medium,
      alignSelf: 'center', marginTop: 12, marginBottom: 8,
    },
    sheetHeader: {
      flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between',
      paddingHorizontal: space.xl, paddingVertical: space.md,
      borderBottomWidth: 1, borderBottomColor: colors.border.subtle,
    },
    sheetTitle: {
      fontFamily: font.bold, fontSize: 18, color: colors.text.primary,
    },
    closeBtn: {
      width: 32, height: 32, borderRadius: 16,
      backgroundColor: colors.bg.card,
      alignItems: 'center', justifyContent: 'center',
    },
    textArea: {
      padding: space.xl, paddingBottom: space.md,
    },
    extractedText: {
      fontFamily: font.regular, fontSize: 14, color: colors.text.primary,
      lineHeight: 22,
    },
    charCount: {
      fontFamily: font.medium, fontSize: 11, color: colors.text.tertiary,
      marginTop: space.sm,
    },
    actions: {
      flexDirection: 'row', gap: space.sm,
      paddingHorizontal: space.xl, paddingTop: space.md,
    },
    copyBtn: {
      flex: 1, flexDirection: 'row', alignItems: 'center', justifyContent: 'center',
      gap: 6, paddingVertical: 12,
      borderRadius: radius.md,
      backgroundColor: copied ? colors.accent.success : colors.accent.primary,
    },
    copyLabel: {
      fontFamily: font.bold, fontSize: 14, color: '#FFFFFF',
    },
    selectBtn: {
      flexDirection: 'row', alignItems: 'center', justifyContent: 'center',
      gap: 6, paddingVertical: 12, paddingHorizontal: 16,
      borderRadius: radius.md,
      backgroundColor: colors.bg.card,
      borderWidth: 1, borderColor: colors.border.subtle,
    },
    selectLabel: {
      fontFamily: font.semibold, fontSize: 13, color: colors.text.secondary,
    },
  }), [colors, copied]);

  // Render button based on variant
  const renderButton = () => {
    if (loading) {
      return (
        <View style={variant === 'icon' ? s.iconBtn : variant === 'full' ? s.fullBtn : s.chipBtn}>
          <ActivityIndicator size="small" color={colors.accent.info} />
          {variant !== 'icon' && <Text style={variant === 'full' ? s.fullLabel : s.chipLabel}>Extracting...</Text>}
        </View>
      );
    }

    switch (variant) {
      case 'icon':
        return (
          <Pressable style={s.iconBtn} onPress={handleExtract} hitSlop={8}
            accessibilityLabel="Extract text from image" accessibilityRole="button">
            <Ionicons name="text-outline" size={20} color={colors.accent.info} />
          </Pressable>
        );
      case 'full':
        return (
          <Pressable style={s.fullBtn} onPress={handleExtract}
            accessibilityLabel="Extract text from image" accessibilityRole="button">
            <Ionicons name="text-outline" size={18} color={colors.accent.info} />
            <Text style={s.fullLabel}>{label || 'Extract Text (OCR)'}</Text>
          </Pressable>
        );
      default: // chip
        return (
          <Pressable style={s.chipBtn} onPress={handleExtract}
            accessibilityLabel="Extract text from image" accessibilityRole="button">
            <Ionicons name="text-outline" size={14} color={colors.accent.info} />
            <Text style={s.chipLabel}>{label || 'OCR'}</Text>
          </Pressable>
        );
    }
  };

  return (
    <>
      {renderButton()}

      {/* Result Modal — Bottom Sheet */}
      <Modal visible={modalVisible} transparent animationType="slide" onRequestClose={handleClose}>
        <Pressable style={s.overlay} onPress={handleClose}>
          <Pressable style={s.sheet} onPress={e => e.stopPropagation()}>
            <View style={s.handle} />
            <View style={s.sheetHeader}>
              <Text style={s.sheetTitle}>Extracted Text</Text>
              <Pressable style={s.closeBtn} onPress={handleClose} hitSlop={8}>
                <Ionicons name="close" size={18} color={colors.text.secondary} />
              </Pressable>
            </View>

            <ScrollView style={s.textArea} showsVerticalScrollIndicator={true}>
              <Animated.View entering={FadeInDown.duration(300)}>
                <Text style={s.extractedText} selectable>{resultText}</Text>
                <Text style={s.charCount}>
                  {resultText?.split(/\s+/).filter(Boolean).length || 0} words • {resultText?.length || 0} characters
                </Text>
              </Animated.View>
            </ScrollView>

            <View style={s.actions}>
              <Pressable style={s.copyBtn} onPress={handleCopy}>
                <Ionicons name={copied ? 'checkmark-circle' : 'copy-outline'} size={18} color="#FFF" />
                <Text style={s.copyLabel}>{copied ? 'Copied!' : 'Copy All'}</Text>
              </Pressable>
              <Pressable style={s.selectBtn} onPress={handleClose}>
                <Ionicons name="close-outline" size={18} color={colors.text.secondary} />
                <Text style={s.selectLabel}>Done</Text>
              </Pressable>
            </View>
          </Pressable>
        </Pressable>
      </Modal>
    </>
  );
}
