import React from 'react';
import { ScrollView, Text, Pressable } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import { useAppTheme } from '../../hooks/useAppTheme';
import createHomeStyles from '../../styles/homeStyles';

interface QuickActionChipsProps {
  onScanDocument: () => void;
  onSendFile: () => void;
  onPdfTools: () => void;
  onToolbox?: () => void;
  onQrScan?: () => void;
}

export default function QuickActionChips({
  onScanDocument,
  onSendFile,
  onPdfTools,
  onToolbox,
  onQrScan,
}: QuickActionChipsProps) {
  const { colors, shadows } = useAppTheme();
  const styles = createHomeStyles(colors, shadows);

  const handlePress = (action?: () => void) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    if (typeof action === 'function') {
      try {
        action();
      } catch (err) {
        console.error('QuickActionChip action error:', err);
      }
    }
  };

  return (
    <ScrollView 
      horizontal 
      showsHorizontalScrollIndicator={false}
      contentContainerStyle={styles.chipsRow}
    >
      <Pressable style={styles.chip} onPress={() => handlePress(onScanDocument)}>
        <Ionicons name="scan-outline" size={16} color={colors.text.secondary} />
        <Text style={styles.chipLabel}>Scan Document</Text>
      </Pressable>

      <Pressable style={styles.chip} onPress={() => handlePress(onSendFile)}>
        <Ionicons name="cloud-upload-outline" size={16} color={colors.text.secondary} />
        <Text style={styles.chipLabel}>Send File</Text>
      </Pressable>

      <Pressable style={styles.chip} onPress={() => handlePress(onPdfTools)}>
        <Ionicons name="document-attach-outline" size={16} color={colors.text.secondary} />
        <Text style={styles.chipLabel}>PDF Tools</Text>
      </Pressable>

      {onToolbox && (
        <Pressable style={styles.chip} onPress={() => handlePress(onToolbox)}>
          <Ionicons name="construct-outline" size={16} color={colors.text.secondary} />
          <Text style={styles.chipLabel}>Toolbox</Text>
        </Pressable>
      )}

      {onQrScan && (
        <Pressable style={styles.chip} onPress={() => handlePress(onQrScan)}>
          <Ionicons name="qr-code-outline" size={16} color={colors.text.secondary} />
          <Text style={styles.chipLabel}>QR Scan</Text>
        </Pressable>
      )}
    </ScrollView>
  );
}
