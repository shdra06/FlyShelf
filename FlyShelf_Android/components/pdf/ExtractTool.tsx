import React, { useState, useMemo } from 'react';
import { View, Text, ScrollView, Pressable, Alert, ActivityIndicator } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as FileSystem from 'expo-file-system/legacy';

import { useAppTheme } from '../../hooks/useAppTheme';
import { createPdfToolsStyles } from '../../styles/pdfToolsStyles';
import { getPdfPageInfo, extractPages } from '../../utils/pdfUtils';
import { SelectedFile, PageEntry } from './types';
import ResultView from './ResultView';
import ProcessingOverlay from './ProcessingOverlay';

const OUTPUT_DIR = `${FileSystem.documentDirectory}FlyShelf/PDFTools/`;

interface ExtractToolProps {
  onBack: () => void;
  onPickFile: () => Promise<SelectedFile[]>;
  saveRecent: (name: string, path: string, pages: number, tool: 'extract') => void;
  onSendToPc?: (filePath: string) => void;
}

export default function ExtractTool({ onBack, onPickFile, saveRecent, onSendToPc }: ExtractToolProps) {
  const { colors, shadows } = useAppTheme();
  const s = useMemo(() => createPdfToolsStyles(colors, shadows), [colors, shadows]);

  const [file, setFile] = useState<SelectedFile | null>(null);
  const [pages, setPages] = useState<PageEntry[]>([]);
  const [loadingPages, setLoadingPages] = useState(false);
  const [loading, setLoading] = useState(false);
  const [resultPath, setResultPath] = useState<string | null>(null);

  const handlePick = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const files = await onPickFile();
    if (files.length) {
      setFile(files[0]);
      setLoadingPages(true);
      try {
        const info = await getPdfPageInfo(files[0].uri);
        setPages(info.pages.map((p, i) => ({ ...p, index: i, originalIndex: i, rotation: 0, selected: false, source: 'original' as const })));
      } catch (e: any) {
        Alert.alert('Error', 'Failed to load PDF');
      } finally {
        setLoadingPages(false);
      }
    }
  };

  const togglePage = (idx: number) => {
    setPages(prev => prev.map((p, i) => i === idx ? { ...p, selected: !p.selected } : p));
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
  };

  const selectedCount = pages.filter(p => p.selected).length;
  const allSelected = pages.length > 0 && selectedCount === pages.length;

  const toggleSelectAll = () => {
    const newVal = !allSelected;
    setPages(prev => prev.map(p => ({ ...p, selected: newVal })));
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
  };

  const handleExtract = async () => {
    if (!file) return;
    const selected = pages.filter(p => p.selected).map(p => p.index + 1);
    if (!selected.length) {
      Alert.alert('Selection Required', 'Please select at least one page to extract.');
      return;
    }
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setLoading(true);
    try {
      await FileSystem.makeDirectoryAsync(OUTPUT_DIR, { intermediates: true }).catch(() => {});
      const outPath = `${OUTPUT_DIR}extracted_${Date.now()}.pdf`;
      await extractPages(file.uri, selected, outPath);
      setResultPath(outPath);
      saveRecent(file.name, outPath, selected.length, 'extract');
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    } catch (e: any) {
      Alert.alert('Extract Failed', e.message);
    } finally {
      setLoading(false);
    }
  };

  if (resultPath) {
    return (
      <View style={s.modalOverlay}>
        <View style={s.modalHeader}>
          <Pressable style={s.backBtn} onPress={onBack} accessibilityRole="button" accessibilityLabel="Go back"><Ionicons name="arrow-back" size={24} color={colors.text.primary} /></Pressable>
          <Text style={s.modalTitle}>Success</Text>
        </View>
        <ResultView path={resultPath} onDone={onBack} onSendToPc={onSendToPc} />
      </View>
    );
  }

  return (
    <View style={s.modalOverlay}>
      <ProcessingOverlay visible={loading} text="Extracting pages…" />
      <View style={s.modalHeader}>
        <Pressable style={s.backBtn} onPress={onBack} accessibilityRole="button" accessibilityLabel="Go back"><Ionicons name="arrow-back" size={24} color={colors.text.primary} /></Pressable>
        <Text style={s.modalTitle}>Extract Pages</Text>
      </View>
      <ScrollView style={s.modalScroll} contentContainerStyle={s.pb100}>
        {!file ? (
          <PickButton label="Pick PDF" onPress={handlePick} s={s} />
        ) : loadingPages ? (
          <ActivityIndicator size="large" color={colors.accent.primary} style={s.mt20} />
        ) : (
          <>
            <View style={s.fileItem}>
              <Ionicons name="document" size={20} color={colors.type.pdf} style={s.fileIcon} />
              <View style={s.fileInfo}>
                <Text style={s.fileName}>{file.name}</Text>
                <Text style={s.fileMeta}>{pages.length} pages</Text>
              </View>
              <Pressable style={s.btnSmall} onPress={() => setFile(null)} accessibilityRole="button" accessibilityLabel="Clear selected file">
                <Ionicons name="close" size={20} color={colors.text.secondary} />
              </Pressable>
            </View>

            {/* Select All / Deselect All toggle + count */}
            <View style={[s.inputRow, s.mt16, { justifyContent: 'space-between' }]}>
              <Text style={s.label}>{selectedCount} of {pages.length} pages selected</Text>
              <Pressable
                style={s.btnSmall}
                onPress={toggleSelectAll}
                accessibilityRole="button"
                accessibilityLabel={allSelected ? 'Deselect all pages' : 'Select all pages'}
              >
                <Text style={{ fontFamily: 'Inter_500Medium', fontSize: 12, color: colors.accent.primary }}>
                  {allSelected ? 'Deselect All' : 'Select All'}
                </Text>
              </Pressable>
            </View>

            <Text style={[s.label, s.mt8]}>Select pages to extract:</Text>
            {pages.map((p, i) => (
              <Pressable key={i} style={s.pageCard} onPress={() => togglePage(i)} accessibilityRole="button" accessibilityLabel={`Toggle page ${i + 1}`}>
                <View style={[s.checkbox, p.selected && s.checkboxChecked]}>
                  {p.selected && <Ionicons name="checkmark" size={14} color="#fff" />}
                </View>
                <View style={s.pageNum}><Text style={s.pageNumText}>{i + 1}</Text></View>
                <View style={s.pageInfo}>
                  <Text style={s.pageSize}>{Math.round(p.width)} × {Math.round(p.height)}</Text>
                </View>
              </Pressable>
            ))}
          </>
        )}
      </ScrollView>
      {file && pages.some(p => p.selected) && !loadingPages && (
        <View style={s.modalActions}>
          <Pressable style={s.btnPrimary} onPress={handleExtract} accessibilityRole="button" accessibilityLabel="Extract pages">
            <Text style={s.btnPrimaryText}>Extract {selectedCount} Pages</Text>
          </Pressable>
        </View>
      )}
    </View>
  );
}

const PickButton = ({ label, onPress, s }: { label: string; onPress: () => void; s: any }) => (
  <Pressable style={[s.btnPrimary, s.mb16]} onPress={onPress} accessibilityRole="button" accessibilityLabel={label}>
    <Text style={s.btnPrimaryText}>{label}</Text>
  </Pressable>
);
