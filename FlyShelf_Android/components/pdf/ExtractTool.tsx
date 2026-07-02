import React, { useState } from 'react';
import { View, Text, ScrollView, Pressable, Alert, ActivityIndicator } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as FileSystem from 'expo-file-system/legacy';
import { colors } from '../../styles/theme';
import s from '../../styles/pdfToolsStyles';
import { getPdfPageInfo, extractPages } from '../../utils/pdfUtils';
import { SelectedFile, PageEntry } from './types';
import ResultView from './ResultView';

const OUTPUT_DIR = `${FileSystem.documentDirectory}FlyShelf/PDFTools/`;

interface ExtractToolProps {
  onBack: () => void;
  onPickFile: () => Promise<SelectedFile[]>;
  saveRecent: (name: string, path: string, pages: number, tool: 'extract') => void;
}

export default function ExtractTool({ onBack, onPickFile, saveRecent }: ExtractToolProps) {
  const [file, setFile] = useState<SelectedFile | null>(null);
  const [pages, setPages] = useState<PageEntry[]>([]);
  const [loading, setLoading] = useState(false);
  const [resultPath, setResultPath] = useState<string | null>(null);

  const handlePick = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const files = await onPickFile();
    if (files.length) {
      setFile(files[0]);
      setLoading(true);
      try {
        const info = await getPdfPageInfo(files[0].uri);
        setPages(info.pages.map((p, i) => ({ ...p, index: i, rotation: 0, selected: false })));
      } catch (e: any) {
        Alert.alert('Error', 'Failed to load PDF');
      } finally {
        setLoading(false);
      }
    }
  };

  const togglePage = (idx: number) => {
    setPages(prev => prev.map((p, i) => i === idx ? { ...p, selected: !p.selected } : p));
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
        <Text style={s.modalTitle}>Extract Pages</Text>
      </View>
      <ScrollView style={s.modalScroll} contentContainerStyle={s.pb100}>
        {!file ? (
          <PickButton label="Pick PDF" onPress={handlePick} />
        ) : loading ? (
          <ActivityIndicator size="large" color={colors.accent.primary} style={s.mt20} />
        ) : (
          <>
            <View style={s.fileItem}>
              <Ionicons name="document" size={20} color={colors.type.pdf} style={s.fileIcon} />
              <View style={s.fileInfo}>
                <Text style={s.fileName}>{file.name}</Text>
                <Text style={s.fileMeta}>{pages.length} pages</Text>
              </View>
              <Pressable style={s.btnSmall} onPress={() => setFile(null)}>
                <Ionicons name="close" size={20} color={colors.text.secondary} />
              </Pressable>
            </View>
            <Text style={[s.label, s.mt16]}>Select pages to extract:</Text>
            {pages.map((p, i) => (
              <Pressable key={i} style={s.pageCard} onPress={() => togglePage(i)}>
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
      {file && pages.some(p => p.selected) && !loading && (
        <View style={s.modalActions}>
          <Pressable style={s.btnPrimary} onPress={handleExtract}>
            <Text style={s.btnPrimaryText}>Extract {pages.filter(p => p.selected).length} Pages</Text>
          </Pressable>
        </View>
      )}
    </View>
  );
}

const PickButton = ({ label, onPress }: { label: string; onPress: () => void }) => (
  <Pressable style={[s.btnPrimary, s.mb16]} onPress={onPress}>
    <Text style={s.btnPrimaryText}>{label}</Text>
  </Pressable>
);
