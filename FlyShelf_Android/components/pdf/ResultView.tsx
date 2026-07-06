import React from 'react';
import { View, Text, Pressable, Alert } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as Sharing from 'expo-sharing';
import { colors } from '../../styles/theme';
import s from '../../styles/pdfToolsStyles';

interface ResultViewProps {
  path?: string | null;
  paths?: string[];
  onDone: () => void;
  onSendToPc?: (filePath: string) => void;
}

export default function ResultView({ path, paths, onDone, onSendToPc }: ResultViewProps) {
  const sharePdf = async (filePath: string) => {
    try {
      Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
      if (await Sharing.isAvailableAsync()) {
        await Sharing.shareAsync(filePath, { mimeType: 'application/pdf' });
      } else {
        Alert.alert('Error', 'Sharing not available on this device');
      }
    } catch (err: any) {
      Alert.alert('Share Failed', err?.message || 'An unexpected error occurred while sharing the PDF.');
    }
  };

  const handleSendToPc = (filePath: string) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    onSendToPc?.(filePath);
  };

  if (!path && !(paths && paths.length)) return null;

  return (
    <View style={s.resultContainer}>
      <Ionicons name="checkmark-circle" size={64} color={colors.accent.success} style={s.resultIcon} />
      <Text style={s.resultTitle}>Success!</Text>
      
      {path && (
        <>
          <Text style={s.resultFile}>{path.split('/').pop()}</Text>
          <View style={[s.resultActions, s.ph20]}>
            <Pressable style={s.btnPrimary} onPress={() => sharePdf(path)}>
              <Text style={s.btnPrimaryText}>Share</Text>
            </Pressable>
            {onSendToPc && (
              <Pressable style={s.btnSecondary} onPress={() => handleSendToPc(path)}>
                <Ionicons name="desktop-outline" size={18} color={colors.text.secondary} style={{ marginRight: 6 }} />
                <Text style={s.btnSecondaryText}>Send to PC</Text>
              </Pressable>
            )}
            <Pressable style={s.btnSecondary} onPress={onDone}>
              <Text style={s.btnSecondaryText}>Done</Text>
            </Pressable>
          </View>
        </>
      )}

      {paths && paths.length > 0 && (
        <>
          {paths.map((p, i) => (
            <View key={i} style={[s.fileItem, s.mt8]}>
              <Ionicons name="document" size={20} color={colors.type.pdf} style={s.fileIcon} />
              <Text style={[s.fileName, s.flex1]} numberOfLines={1}>{p.split('/').pop()}</Text>
              {onSendToPc && (
                <Pressable style={s.btnSmall} onPress={() => handleSendToPc(p)}>
                  <Ionicons name="desktop-outline" size={16} color={colors.accent.primary} />
                </Pressable>
              )}
              <Pressable style={s.btnSmall} onPress={() => sharePdf(p)}>
                <Ionicons name="share-outline" size={16} color={colors.accent.primary} />
              </Pressable>
            </View>
          ))}
          <View style={[s.resultActions, s.mt16, s.ph20]}>
            <Pressable style={s.btnSecondary} onPress={onDone}>
              <Text style={s.btnSecondaryText}>Done</Text>
            </Pressable>
          </View>
        </>
      )}
    </View>
  );
}
