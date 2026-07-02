import React, { useState } from 'react';
import { View, Text, ScrollView, Pressable, Alert } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as FileSystem from 'expo-file-system/legacy';
import Animated, { FadeInDown } from 'react-native-reanimated';
import { colors } from '../../styles/theme';
import s from '../../styles/pdfToolsStyles';
import { mergePdfs } from '../../utils/pdfUtils';
import { SelectedFile } from './types';
import ResultView from './ResultView';

const OUTPUT_DIR = `${FileSystem.documentDirectory}FlyShelf/PDFTools/`;

interface MergeToolProps {
  onBack: () => void;
  onPickFiles: () => Promise<SelectedFile[]>;
  saveRecent: (name: string, path: string, pages: number, tool: 'merge') => void;
}

export default function MergeTool({ onBack, onPickFiles, saveRecent }: MergeToolProps) {
  const [files, setFiles] = useState<SelectedFile[]>([]);
  const [loading, setLoading] = useState(false);
  const [resultPath, setResultPath] = useState<string | null>(null);

  const handlePick = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const newFiles = await onPickFiles();
    if (newFiles.length) setFiles(prev => [...prev, ...newFiles]);
  };

  const handleMerge = async () => {
    if (files.length < 2) {
      Alert.alert('Selection Required', 'Please select at least 2 PDFs to merge.');
      return;
    }
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setLoading(true);
    try {
      await FileSystem.makeDirectoryAsync(OUTPUT_DIR, { intermediates: true }).catch(() => {});
      const outPath = `${OUTPUT_DIR}merged_${Date.now()}.pdf`;
      await mergePdfs(files.map(f => f.uri), outPath);
      setResultPath(outPath);
      saveRecent('merged.pdf', outPath, files.length, 'merge');
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    } catch (e: any) {
      Alert.alert('Merge Failed', e.message);
    } finally {
      setLoading(false);
    }
  };

  const moveFile = (idx: number, dir: -1 | 1) => {
    const ni = idx + dir;
    if (ni < 0 || ni >= files.length) return;
    const arr = [...files];
    [arr[idx], arr[ni]] = [arr[ni], arr[idx]];
    setFiles(arr);
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
  };

  const removeFile = (idx: number) => {
    setFiles(prev => prev.filter((_, i) => i !== idx));
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
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
        <Text style={s.modalTitle}>Merge PDFs</Text>
      </View>
      <ScrollView style={s.modalScroll} contentContainerStyle={s.pb100}>
        <Pressable style={[s.btnPrimary, s.mb16]} onPress={handlePick}>
          <Text style={s.btnPrimaryText}>+ Add PDFs</Text>
        </Pressable>
        {files.map((f, i) => (
          <Animated.View key={`${f.uri}-${i}`} entering={FadeInDown.delay(i * 50)} style={s.fileItem}>
            <Ionicons name="document" size={20} color={colors.type.pdf} style={s.fileIcon} />
            <View style={s.fileInfo}>
              <Text style={s.fileName} numberOfLines={1}>{f.name}</Text>
              {f.size ? <Text style={s.fileMeta}>{(f.size / 1024).toFixed(0)} KB</Text> : null}
            </View>
            <View style={s.fileActions}>
              <Pressable style={s.btnSmall} onPress={() => moveFile(i, -1)} disabled={i === 0}>
                <Ionicons name="arrow-up" size={16} color={i === 0 ? colors.text.disabled : colors.text.secondary} />
              </Pressable>
              <Pressable style={s.btnSmall} onPress={() => moveFile(i, 1)} disabled={i === files.length - 1}>
                <Ionicons name="arrow-down" size={16} color={i === files.length - 1 ? colors.text.disabled : colors.text.secondary} />
              </Pressable>
              <Pressable style={s.btnSmall} onPress={() => removeFile(i)}>
                <Ionicons name="trash-outline" size={16} color={colors.accent.error} />
              </Pressable>
            </View>
          </Animated.View>
        ))}
      </ScrollView>
      {files.length >= 2 && (
        <View style={s.modalActions}>
          <Pressable style={s.btnPrimary} onPress={handleMerge} disabled={loading}>
            <Text style={s.btnPrimaryText}>{loading ? 'Merging...' : `Merge ${files.length} PDFs`}</Text>
          </Pressable>
        </View>
      )}
    </View>
  );
}
