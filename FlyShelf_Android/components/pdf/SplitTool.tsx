import { useAppTheme } from '../../hooks/useAppTheme';
import React, { useState, useMemo } from 'react';
import { View, Text, ScrollView, Pressable, TextInput, Alert } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';

import { createPdfToolsStyles } from '../../styles/pdfToolsStyles';
import { getPdfPageInfo } from '../../utils/pdfUtils';
import { splitPdf } from '../../utils/pdfToolsUtils';
import { SelectedFile } from './types';
import ResultView from './ResultView';
import ProcessingOverlay from './ProcessingOverlay';

interface SplitToolProps {
  onBack: () => void;
  onPickFile: () => Promise<SelectedFile[]>;
  saveRecent?: (name: string, path: string, pages: number, tool: 'split') => void;
}

export default function SplitTool({ onBack, onPickFile, saveRecent }: SplitToolProps) {
  const { colors, shadows } = useAppTheme();
  const s = useMemo(() => createPdfToolsStyles(colors, shadows), [colors, shadows]);

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

  /** Preset: one page per split — "1, 2, 3, ..., N" */
  const presetEveryPage = () => {
    if (pageCount <= 0) return;
    const r = Array.from({ length: pageCount }, (_, i) => String(i + 1)).join(', ');
    setRanges(r);
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
  };

  /** Preset: first half / second half */
  const presetHalves = () => {
    if (pageCount <= 1) return;
    const mid = Math.ceil(pageCount / 2);
    setRanges(`1-${mid}, ${mid + 1}-${pageCount}`);
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
  };

  const handleSplit = async () => {
    if (!file || !ranges.trim()) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);

    const splitRanges = ranges.split(',').map(r => {
      const parts = r.trim().split('-').map(Number);
      return { start: parts[0], end: parts.length > 1 ? parts[1] : parts[0] };
    }).filter(r => !isNaN(r.start) && !isNaN(r.end));
    
    if (!splitRanges.length) {
      Alert.alert('Invalid Format', 'Please use formats like 1-3, 4-6');
      return;
    }

    // Validate ranges against page count
    if (pageCount > 0) {
      for (const r of splitRanges) {
        if (r.start < 1 || r.end < 1 || r.start > pageCount || r.end > pageCount) {
          Alert.alert(
            'Out of Bounds',
            `Page range ${r.start}-${r.end} is invalid. This PDF has ${pageCount} pages (1–${pageCount}).`
          );
          return;
        }
        if (r.start > r.end) {
          Alert.alert('Invalid Range', `Start page (${r.start}) cannot be greater than end page (${r.end}).`);
          return;
        }
      }
    }

    setLoading(true);
    try {
      const paths = await splitPdf(file.uri, splitRanges);
      setResultPaths(paths);
      saveRecent?.(file.name, file.uri, pageCount, 'split');
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
          <Pressable style={s.backBtn} onPress={onBack} accessibilityRole="button" accessibilityLabel="Go back"><Ionicons name="arrow-back" size={24} color={colors.text.primary} /></Pressable>
          <Text style={s.modalTitle}>Success</Text>
        </View>
        <ResultView paths={resultPaths} onDone={onBack} />
      </View>
    );
  }

  return (
    <View style={s.modalOverlay}>
      <ProcessingOverlay visible={loading} text="Splitting PDF…" />
      <View style={s.modalHeader}>
        <Pressable style={s.backBtn} onPress={onBack} accessibilityRole="button" accessibilityLabel="Go back"><Ionicons name="arrow-back" size={24} color={colors.text.primary} /></Pressable>
        <Text style={s.modalTitle}>Split PDF</Text>
      </View>
      <ScrollView style={s.modalScroll}>
        {!file ? (
          <Pressable style={[s.btnPrimary, s.mb16]} onPress={handlePick} accessibilityRole="button" accessibilityLabel="Pick PDF">
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
              <Pressable style={s.btnSmall} onPress={() => setFile(null)} accessibilityRole="button" accessibilityLabel="Clear selected file">
                <Ionicons name="close" size={20} color={colors.text.secondary} />
              </Pressable>
            </View>

            {/* Quick Presets */}
            {pageCount > 0 && (
              <>
                <Text style={[s.label, s.mt16]}>Quick Presets</Text>
                <View style={s.inputRow}>
                  <Pressable
                    style={[s.btnSecondary, { flex: 1 }]}
                    onPress={presetEveryPage}
                    accessibilityRole="button"
                    accessibilityLabel="Split every page"
                  >
                    <Text style={s.btnSecondaryText}>Every Page</Text>
                  </Pressable>
                  <Pressable
                    style={[s.btnSecondary, { flex: 1 }]}
                    onPress={presetHalves}
                    disabled={pageCount <= 1}
                    accessibilityRole="button"
                    accessibilityLabel="Split into halves"
                  >
                    <Text style={[s.btnSecondaryText, pageCount <= 1 && { color: colors.text.disabled }]}>First / Second Half</Text>
                  </Pressable>
                </View>
              </>
            )}

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
          <Pressable style={s.btnPrimary} onPress={handleSplit} disabled={loading} accessibilityRole="button" accessibilityLabel="Split PDF">
            <Text style={s.btnPrimaryText}>Split PDF</Text>
          </Pressable>
        </View>
      )}
    </View>
  );
}
