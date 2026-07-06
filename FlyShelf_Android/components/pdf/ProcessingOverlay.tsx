import React from 'react';
import { Modal, View, Text, ActivityIndicator } from 'react-native';
import { colors } from '../../styles/theme';
import s from '../../styles/pdfToolsStyles';

interface ProcessingOverlayProps {
  visible: boolean;
  text: string;
}

/** Full-screen translucent overlay with animated spinner + operation text */
export default function ProcessingOverlay({ visible, text }: ProcessingOverlayProps) {
  return (
    <Modal transparent visible={visible} animationType="fade" statusBarTranslucent>
      <View style={s.loadingOverlay}>
        <ActivityIndicator size="large" color={colors.accent.primary} />
        <Text style={s.loadingText}>{text}</Text>
      </View>
    </Modal>
  );
}
