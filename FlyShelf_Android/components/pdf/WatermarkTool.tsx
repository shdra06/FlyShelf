import React, { useState } from 'react';
import { View, Text, ScrollView, Pressable, TextInput, Alert, ActivityIndicator } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import { colors } from '../../styles/theme';
import s from '../../styles/pdfToolsStyles';
import { addWatermark } from '../../utils/pdfToolsUtils';
import { SelectedFile } from './types';
import ResultView from './ResultView';

interface WatermarkToolProps {
  onBack: () => void;
  onPickFile: () => Promise<SelectedFile[]>;
  saveRecent: (name: string, path: string, pages: number, tool: 'watermark') => void;
}

export default function WatermarkTool({ onBack, onPickFile, saveRecent }: WatermarkToolProps) {
  const [file, setFile] = useState<SelectedFile | null>(null);
  const [text, setText] = useState('CONFIDENTIAL');
  const [loading, setLoading] = useState(false);
  const [resultPath, setResultPath] = useState<string | null>(null);

  const handlePick = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const files = await onPickFile();
    if (files.length) setFile(files[0]);
  };

  const handleApply = async () => {
    if (!file || !text.trim()) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setLoading(true);
    try {
      const outPath = await addWatermark(file.uri, text, {
        fontSize: 48,
        opacity: 0.15,
        rotation: -45,
        color: { r: 0.5, g: 0.5, b: 0.5 },
      });
      setResultPath(outPath);
      saveRecent(file.name, outPath, 0, 'watermark');
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    } catch (e: any) {
      Alert.alert('Watermark Failed', e.message);
    } finally {
      setLoading(false);
    }
  };

  if (resultPath) {
    return (
      <View style={s.modalOverlay}>
        <View style={s.modalHeader}>
          <Pressable style={s.backBtn} onPress={onBack}><Ionicons name="arrow-back" size={24} color={colors.text.primary} /></Pressable>
          <Text style={s.modalTitle}>Success</Text>
        </View>
        <ResultView path={resultPath} onDone={onBack} />
      </View>
    );
  }

  return (
    <View style={s.modalOverlay}>
      <View style={s.modalHeader}>
        <Pressable style={s.backBtn} onPress={onBack}><Ionicons name="arrow-back" size={24} color={colors.text.primary} /></Pressable>
        <Text style={s.modalTitle}>Watermark PDF</Text>
      </View>
      <ScrollView style={s.modalScroll}>
        {!file ? (
          <Pressable style={s.btnPrimary} onPress={handlePick}>
            <Text style={s.btnPrimaryText}>Pick PDF</Text>
          </Pressable>
        ) : (
          <>
            <View style={s.fileItem}>
              <Ionicons name="document" size={20} color={colors.type.pdf} style={s.fileIcon} />
              <View style={s.fileInfo}>
                <Text style={s.fileName}>{file.name}</Text>
              </View>
              <Pressable style={s.btnSmall} onPress={() => setFile(null)}>
                <Ionicons name="close" size={20} color={colors.text.secondary} />
              </Pressable>
            </View>
            <Text style={[s.label, s.mt16]}>Watermark Text</Text>
            <TextInput
              style={s.input}
              value={text}
              onChangeText={setText}
              placeholder="e.g. CONFIDENTIAL"
              placeholderTextColor={colors.text.tertiary}
            />
          </>
        )}
      </ScrollView>
      {file && text.trim() && (
        <View style={s.modalActions}>
          <Pressable style={s.btnPrimary} onPress={handleApply} disabled={loading}>
            <Text style={s.btnPrimaryText}>{loading ? 'Applying...' : 'Apply Watermark'}</Text>
          </Pressable>
        </View>
      )}
    </View>
  );
}
