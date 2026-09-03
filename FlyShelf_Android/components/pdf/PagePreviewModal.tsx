import React, { useMemo } from 'react';
import {
  Modal,
  View,
  Text,
  Image,
  Pressable,
  StyleSheet,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import { useAppTheme } from '../../hooks/useAppTheme';
import { createPdfEditorStyles } from '../../styles/pdfEditorStyles';
import { PageEntry } from './types';

export interface PagePreviewModalProps {
  visible: boolean;
  onClose: () => void;
  page: PageEntry | null;
  pageNumber: number;
  totalPages: number;
}

export default function PagePreviewModal({
  visible,
  onClose,
  page,
  pageNumber,
  totalPages,
}: PagePreviewModalProps) {
  const { colors, shadows, font } = useAppTheme();
  const styles = useMemo(() => createPdfEditorStyles(colors, shadows), [colors, shadows]);

  const handleClose = () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    onClose();
  };

  const imageUri = page?.thumbnailUri || page?.sourceUri;
  const rotation = page?.rotation ?? 0;

  const localStyles = useMemo(() => StyleSheet.create({
    closeButton: {
      padding: 6,
      borderRadius: 20,
      backgroundColor: colors.bg.elevated,
      alignItems: 'center',
      justifyContent: 'center',
    },
    topBarSpacer: {
      width: 36,
    },
    centerContainer: {
      flex: 1,
      alignItems: 'center',
      justifyContent: 'center',
      paddingTop: 60,
    },
    placeholderContainer: {
      flex: 1,
      alignItems: 'center',
      justifyContent: 'center',
      paddingHorizontal: 32,
    },
    placeholderCard: {
      width: 240,
      height: 340,
      borderRadius: 16,
      alignItems: 'center',
      justifyContent: 'center',
      padding: 24,
      borderWidth: 1,
      borderColor: colors.border.subtle,
      gap: 8,
    },
    placeholderNumber: {
      fontFamily: font.bold,
      fontSize: 22,
      marginTop: 8,
    },
    placeholderSource: {
      fontFamily: font.medium,
      fontSize: 13,
    },
    placeholderDimensions: {
      fontFamily: font.regular,
      fontSize: 12,
    },
  }), [colors, font]);

  return (
    <Modal
      visible={visible}
      transparent={false}
      animationType="fade"
      statusBarTranslucent
      onRequestClose={handleClose}
    >
      <View style={styles.previewContainer}>
        {/* Top Navigation Bar */}
        <View style={styles.previewTopBar}>
          <Pressable
            onPress={handleClose}
            hitSlop={12}
            style={localStyles.closeButton}
            accessibilityLabel="Close preview"
            accessibilityRole="button"
          >
            <Ionicons name="close" size={24} color={colors.text.primary} />
          </Pressable>
          <Text style={styles.previewTitle} numberOfLines={1}>
            Page {pageNumber} of {totalPages}
          </Text>
          <View style={localStyles.topBarSpacer} />
        </View>

        {/* Center Preview Content */}
        <View style={localStyles.centerContainer}>
          {imageUri ? (
            <Image
              source={{ uri: imageUri }}
              style={[
                styles.previewImage,
                rotation !== 0 && { transform: [{ rotate: `${rotation}deg` }] },
              ]}
              resizeMode="contain"
            />
          ) : (
            <View style={localStyles.placeholderContainer}>
              <View style={[localStyles.placeholderCard, { backgroundColor: colors.bg.card }]}>
                <Ionicons
                  name={page?.source === 'blank' ? 'document-outline' : 'document-text-outline'}
                  size={64}
                  color={colors.text.disabled}
                />
                <Text style={[localStyles.placeholderNumber, { color: colors.text.primary }]}>
                  Page {pageNumber}
                </Text>
                {page?.source === 'blank' && (
                  <Text style={[localStyles.placeholderSource, { color: colors.text.tertiary }]}>
                    Blank Page
                  </Text>
                )}
                {!!page?.width && !!page?.height && (
                  <Text style={[localStyles.placeholderDimensions, { color: colors.text.tertiary }]}>
                    {Math.round(page.width)} × {Math.round(page.height)} pt
                  </Text>
                )}
              </View>
            </View>
          )}
        </View>
      </View>
    </Modal>
  );
}
