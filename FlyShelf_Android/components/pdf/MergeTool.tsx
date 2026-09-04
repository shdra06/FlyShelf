import { useAppTheme } from '../../hooks/useAppTheme';
import React, { useState, useMemo } from 'react';
import { View, Text, ScrollView, Pressable, Alert, Image, StyleSheet } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as FileSystem from 'expo-file-system/legacy';
import Animated, { FadeInDown } from 'react-native-reanimated';
import PdfThumbnail from 'react-native-pdf-thumbnail';

import { createPdfToolsStyles } from '../../styles/pdfToolsStyles';
import { mergePdfs, getPdfPageInfo } from '../../utils/pdfUtils';
import { SelectedFile } from './types';
import ResultView from './ResultView';
import ProcessingOverlay from './ProcessingOverlay';
import { font, space, radius } from '../../styles/theme';

const OUTPUT_DIR = `${FileSystem.documentDirectory}FlyShelf/PDFTools/`;

interface MergeToolProps {
  onBack: () => void;
  onPickFiles: () => Promise<SelectedFile[]>;
  saveRecent: (name: string, path: string, pages: number, tool: 'merge') => void;
  onSendToPc?: (filePath: string) => Promise<void>;
}

interface MergeFile extends SelectedFile {
  pageCount?: number;
  thumbnailUri?: string;
}

export default function MergeTool({ onBack, onPickFiles, saveRecent, onSendToPc }: MergeToolProps) {
  const { colors, shadows } = useAppTheme();
  const s = useMemo(() => createPdfToolsStyles(colors, shadows), [colors, shadows]);
  const ls = useMemo(() => StyleSheet.create({
    thumb: {
      width: 48,
      height: 64,
      borderRadius: radius.sm,
      marginRight: space.md,
      backgroundColor: colors.bg.elevated
    },
    badge: {
      backgroundColor: colors.bg.elevated,
      paddingHorizontal: space.sm,
      paddingVertical: 2,
      borderRadius: radius.sm,
      marginTop: space.xs,
      alignSelf: 'flex-start'
    },
    badgeText: {
      fontFamily: font.medium,
      fontSize: 10,
      color: colors.text.secondary
    },
    warningBox: {
      backgroundColor: colors.accent.warningDim,
      padding: space.md,
      borderRadius: radius.md,
      marginVertical: space.md,
      flexDirection: 'row',
      alignItems: 'center'
    },
    warningText: {
      fontFamily: font.medium,
      fontSize: 13,
      color: colors.accent.warning,
      marginLeft: space.sm,
      flex: 1
    }
  }), [colors]);

  const [files, setFiles] = useState<MergeFile[]>([]);
  const [loading, setLoading] = useState(false);
  const [processingText, setProcessingText] = useState('Merging PDFs…');
  const [resultPath, setResultPath] = useState<string | null>(null);

  const handlePick = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const newFiles = await onPickFiles();
    if (!newFiles.length) return;

    setLoading(true);
    setProcessingText('Analyzing PDFs…');
    
    const enrichedFiles: MergeFile[] = [];
    for (const file of newFiles) {
      let pageCount = 0;
      let thumbnailUri = undefined;
      try {
        const info = await getPdfPageInfo(file.uri);
        pageCount = info.pageCount;
      } catch (e) {}
      
      try {
        const result = await PdfThumbnail.generate(file.uri, 0, 100);
        thumbnailUri = result.uri;
      } catch (e) {}

      enrichedFiles.push({ ...file, pageCount, thumbnailUri });
    }
    
    setFiles(prev => [...prev, ...enrichedFiles]);
    setLoading(false);
  };

  const totalPages = files.reduce((acc, f) => acc + (f.pageCount || 0), 0);
  const totalSize = files.reduce((acc, f) => acc + (f.size || 0), 0);
  const isLarge = totalSize > 30 * 1024 * 1024; // > 30MB

  const handleMerge = async () => {
    if (files.length < 2) {
      Alert.alert('Selection Required', 'Please select at least 2 PDFs to merge.');
      return;
    }
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setLoading(true);
    setProcessingText('Merging PDFs…');
    try {
      await FileSystem.makeDirectoryAsync(OUTPUT_DIR, { intermediates: true }).catch(() => {});
      const outPath = `${OUTPUT_DIR}merged_${Date.now()}.pdf`;
      await mergePdfs(files.map(f => f.uri), outPath);
      setResultPath(outPath);
      saveRecent('merged.pdf', outPath, totalPages, 'merge');
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    } catch (e: any) {
      Alert.alert('Merge Failed', e.message);
    } finally {
      setLoading(false);
    }
  };

  const moveFile = (idx: number, dir: -1 | 1) => {
    setFiles(prev => {
      const ni = idx + dir;
      if (ni < 0 || ni >= prev.length) return prev;
      const arr = [...prev];
      [arr[idx], arr[ni]] = [arr[ni], arr[idx]];
      return arr;
    });
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light).catch(() => {});
  };

  const removeFile = (idx: number) => {
    setFiles(prev => prev.filter((_, i) => i !== idx));
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
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
      <ProcessingOverlay visible={loading} text={processingText} />
      <View style={s.modalHeader}>
        <Pressable style={s.backBtn} onPress={onBack} accessibilityRole="button" accessibilityLabel="Go back"><Ionicons name="arrow-back" size={24} color={colors.text.primary} /></Pressable>
        <Text style={s.modalTitle}>Merge PDFs</Text>
      </View>
      <ScrollView style={s.modalScroll} contentContainerStyle={s.pb100}>
        <Pressable style={[s.btnPrimary, s.mb16]} onPress={handlePick} accessibilityRole="button" accessibilityLabel="Add PDFs">
          <Text style={s.btnPrimaryText}>+ Add PDFs</Text>
        </Pressable>
        
        {isLarge && (
          <Animated.View entering={FadeInDown} style={ls.warningBox}>
            <Ionicons name="warning" size={20} color={colors.accent.warning} />
            <Text style={ls.warningText}>Total size exceeds 30MB. This might take a while.</Text>
          </Animated.View>
        )}

        {files.map((f, i) => (
          <Animated.View key={`${f.uri}-${i}`} entering={FadeInDown.delay(i * 50)} style={s.fileItem}>
            {f.thumbnailUri ? (
              <Image source={{ uri: f.thumbnailUri }} style={ls.thumb} resizeMode="cover" />
            ) : (
              <View style={[ls.thumb, { alignItems: 'center', justifyContent: 'center' }]}>
                <Ionicons name="document" size={24} color={colors.type.pdf} />
              </View>
            )}
            
            <View style={s.fileInfo}>
              <Text style={s.fileName} numberOfLines={1}>{f.name}</Text>
              <View style={{ flexDirection: 'row', alignItems: 'center', gap: space.sm }}>
                {f.size ? <Text style={s.fileMeta}>{(f.size / 1024).toFixed(0)} KB</Text> : null}
                {f.pageCount ? (
                  <View style={ls.badge}>
                    <Text style={ls.badgeText}>{f.pageCount} pages</Text>
                  </View>
                ) : null}
              </View>
            </View>
            <View style={s.fileActions}>
              <Pressable style={s.btnSmall} onPress={() => moveFile(i, -1)} disabled={i === 0} accessibilityRole="button" accessibilityLabel="Move up">
                <Ionicons name="arrow-up" size={16} color={i === 0 ? colors.text.disabled : colors.text.secondary} />
              </Pressable>
              <Pressable style={s.btnSmall} onPress={() => moveFile(i, 1)} disabled={i === files.length - 1} accessibilityRole="button" accessibilityLabel="Move down">
                <Ionicons name="arrow-down" size={16} color={i === files.length - 1 ? colors.text.disabled : colors.text.secondary} />
              </Pressable>
              <Pressable style={s.btnSmall} onPress={() => removeFile(i)} accessibilityRole="button" accessibilityLabel="Remove file">
                <Ionicons name="trash-outline" size={16} color={colors.accent.error} />
              </Pressable>
            </View>
          </Animated.View>
        ))}
      </ScrollView>
      {files.length >= 2 && (
        <View style={s.modalActions}>
          <Pressable style={s.btnPrimary} onPress={handleMerge} disabled={loading} accessibilityRole="button" accessibilityLabel="Merge PDFs">
            <Text style={s.btnPrimaryText}>{`Merge ${files.length} PDFs (${totalPages} pages)`}</Text>
          </Pressable>
        </View>
      )}
    </View>
  );
}
