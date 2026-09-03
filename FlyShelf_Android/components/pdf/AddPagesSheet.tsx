import React, { useMemo } from 'react';
import {
  Modal,
  View,
  Text,
  Pressable,
  StyleSheet,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import { useAppTheme } from '../../hooks/useAppTheme';
import { createPdfEditorStyles } from '../../styles/pdfEditorStyles';

export interface AddPagesSheetProps {
  visible: boolean;
  onClose: () => void;
  onScanDocument: () => void;
  onPickImages: () => void;
  onPickPdf: () => void;
  onAddBlankPage: () => void;
}

interface SheetOptionItem {
  id: string;
  icon: keyof typeof Ionicons.glyphMap;
  iconColor: string;
  iconBg: string;
  label: string;
  desc: string;
  onPress: () => void;
}

export default function AddPagesSheet({
  visible,
  onClose,
  onScanDocument,
  onPickImages,
  onPickPdf,
  onAddBlankPage,
}: AddPagesSheetProps) {
  const { colors, shadows } = useAppTheme();
  const styles = useMemo(() => createPdfEditorStyles(colors, shadows), [colors, shadows]);

  const handleOptionPress = (action: () => void) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    action();
  };

  const handleClose = () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    onClose();
  };

  const options: SheetOptionItem[] = [
    {
      id: 'scan',
      icon: 'camera-outline',
      iconColor: colors.accent.primary,
      iconBg: colors.accent.primaryDim,
      label: 'Scan Document',
      desc: 'Use camera to scan pages',
      onPress: onScanDocument,
    },
    {
      id: 'gallery',
      icon: 'images-outline',
      iconColor: colors.type.image ?? '#A78BFA',
      iconBg: colors.type.image ? `${colors.type.image}1F` : 'rgba(167, 139, 250, 0.12)',
      label: 'From Gallery',
      desc: 'Add photos as PDF pages',
      onPress: onPickImages,
    },
    {
      id: 'pdf',
      icon: 'document-text-outline',
      iconColor: colors.type.pdf ?? colors.accent.error,
      iconBg: colors.accent.errorDim,
      label: 'From PDF File',
      desc: 'Import pages from another PDF',
      onPress: onPickPdf,
    },
    {
      id: 'blank',
      icon: 'document-outline',
      iconColor: colors.accent.success,
      iconBg: colors.accent.successDim,
      label: 'Blank Page',
      desc: 'Insert an empty page',
      onPress: onAddBlankPage,
    },
  ];

  return (
    <Modal
      visible={visible}
      transparent
      animationType="slide"
      statusBarTranslucent
      onRequestClose={handleClose}
    >
      <View style={localStyles.overlay}>
        <Pressable
          style={localStyles.backdrop}
          onPress={handleClose}
          accessibilityLabel="Dismiss sheet"
          accessibilityRole="button"
        />
        <View style={styles.sheetContainer}>
          <View style={styles.sheetHandle} />
          <Text style={styles.sheetTitle}>Add Pages</Text>

          {options.map((opt) => (
            <Pressable
              key={opt.id}
              onPress={() => handleOptionPress(opt.onPress)}
              style={({ pressed }) => [
                styles.sheetOption,
                pressed && { backgroundColor: colors.bg.cardHover },
              ]}
              accessibilityRole="button"
              accessibilityLabel={`${opt.label}, ${opt.desc}`}
            >
              <View style={[styles.sheetOptionIcon, { backgroundColor: opt.iconBg }]}>
                <Ionicons name={opt.icon} size={22} color={opt.iconColor} />
              </View>
              <View style={styles.sheetOptionTextWrap}>
                <Text style={styles.sheetOptionLabel}>{opt.label}</Text>
                <Text style={styles.sheetOptionDesc}>{opt.desc}</Text>
              </View>
              <Ionicons name="chevron-forward" size={18} color={colors.text.tertiary} />
            </Pressable>
          ))}
        </View>
      </View>
    </Modal>
  );
}

const localStyles = StyleSheet.create({
  overlay: {
    flex: 1,
    justifyContent: 'flex-end',
  },
  backdrop: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: 'rgba(0, 0, 0, 0.55)',
  },
});
