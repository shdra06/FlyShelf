import React, { useState } from 'react';
import { View, Text, ScrollView, Pressable, TextInput, Alert } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import { colors } from '../../styles/theme';
import s from '../../styles/pdfToolsStyles';
import { getPdfPageInfo } from '../../utils/pdfUtils';
import { splitPdf } from '../../utils/pdfToolsUtils';
import { SelectedFile } from './types';
import ResultView from './ResultView';

interface SplitToolProps {
  onBack: () => void;
  onPickFile: () => Promise<SelectedFile[]>;
}

export default function SplitTool({ onBack, onPickFile }: SplitToolProps) {
  const [file, setFile] = useState<SelectedFile | null>(null);
  const [pageCount, setPageCount] = useState(0);
  const [ranges, setRanges] = useState('');
  const [loading, setLoading] = useState(false);
  const [resultPaths, setResultPaths] = useState<string[]>([]);

  const handlePick = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const files = await onPickFile();
    if (files.length) {
      setFile(files[0]);
      try {
        const info = await getPdfPageInfo(files[0].uri);
        setPageCount(info.pageCount);
      } catch (e: any) {
        Alert.alert('Error', 'Failed to read PDF info');
      }
    }
  };

  const handleSplit = async () => {
    if (!file || !ranges.trim()) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setLoading(true);
    try {
      const splitRanges = ranges.split(',').map(r => {
        const parts = r.trim().split('-').map(Number);
        return { start: parts[0], end: parts.length > 1 ? parts[1] : parts[0] };
      }).filter(r => !isNaN(r.start) && !isNaN(r.end));
      
      if (!splitRanges.length) {
        Alert.alert('Invalid Format', 'Please use formats like 1-3, 4-6');
        setLoading(false);
        return;
      }

      const paths = await splitPdf(file.uri, splitRanges);
      setResultPaths(paths);
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    } catch (e: any) {
      Alert.alert('Split Failed', e.message);
    } finally {
      setLoading(false);
    }
  };

  if (resultPaths.length > 0) {
    return (
      <View style={s.modalOverlay}>
        <View style={s.modalHeader}>
          <Pressable style={s.backBtn} onPress={onBack}><Ionicons name="arrow-back" size={24} color={colors.text.primary} /></Pressable>
          <Text style={s.modalTitle}>Success</Text>
        </View>
        <ResultView paths={resultPaths} onDone={onBack} />
      </View>
    );
  }

  return (
    <View style={s.modalOverlay}>
      <View style={s.modalHeader}>
        <Pressable style={s.backBtn} onPress={onBack}><Ionicons name="arrow-back" size={24} color={colors.text.primary} /></Pressable>
        <Text style={s.modalTitle}>Split PDF</Text>
      </View>
      <ScrollView style={s.modalScroll}>
        {!file ? (
          <Pressable style={[s.btnPrimary, s.mb16]} onPress={handlePick}>
            <Text style={s.btnPrimaryText}>Pick PDF</Text>
          </Pressable>
        ) : (
          <>
            <View style={s.fileItem}>
              <Ionicons name="document" size={20} color={colors.type.pdf} style={s.fileIcon} />
              <View style={s.fileInfo}>
                <Text style={s.fileName}>{file.name}</Text>
                <Text style={s.fileMeta}>{pageCount} pages</Text>
              </View>
              <Pressable style={s.btnSmall} onPress={() => setFile(null)}>
                <Ionicons name="close" size={20} color={colors.text.secondary} />
              </Pressable>
            </View>
            <Text style={[s.label, s.mt16]}>Page Ranges (e.g., 1-3, 4-6)</Text>
            <TextInput
              style={s.input}
              value={ranges}
              onChangeText={setRanges}
              placeholder="e.g. 1-2, 3-5"
              placeholderTextColor={colors.text.tertiary}
            />
          </>
        )}
      </ScrollView>
      {file && ranges.trim() && (
        <View style={s.modalActions}>
          <Pressable style={s.btnPrimary} onPress={handleSplit} disabled={loading}>
            <Text style={s.btnPrimaryText}>{loading ? 'Splitting...' : 'Split PDF'}</Text>
          </Pressable>
        </View>
      )}
    </View>
  );
}
