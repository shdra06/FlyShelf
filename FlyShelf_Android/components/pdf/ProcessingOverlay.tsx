import { useAppTheme } from '../../hooks/useAppTheme';
import React, { useMemo } from 'react';
import { Modal, View, Text, ActivityIndicator } from 'react-native';

import { createPdfToolsStyles } from '../../styles/pdfToolsStyles';

interface ProcessingOverlayProps {
  visible: boolean;
  text: string;
}

/** Full-screen translucent overlay with animated spinner + operation text */
export default function ProcessingOverlay({ visible, text }: ProcessingOverlayProps) {
  const { colors, shadows } = useAppTheme();
  const s = useMemo(() => createPdfToolsStyles(colors, shadows), [colors, shadows]);

  return (
    <Modal transparent visible={visible} animationType="fade" statusBarTranslucent>
      <View style={s.loadingOverlay}>
        <ActivityIndicator size="large" color={colors.accent.primary} />
        <Text style={s.loadingText}>{text}</Text>
      </View>
    </Modal>
  );
}
