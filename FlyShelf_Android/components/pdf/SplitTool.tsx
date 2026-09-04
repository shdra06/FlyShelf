import React, { useState, useMemo, useEffect } from 'react';
import { View, Text, ScrollView, Pressable, TextInput, Alert, StyleSheet, Image, ActivityIndicator } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import Animated, { FadeInDown } from 'react-native-reanimated';
import PdfThumbnail from 'react-native-pdf-thumbnail';

import { useAppTheme } from '../../hooks/useAppTheme';
import { createPdfToolsStyles } from '../../styles/pdfToolsStyles';
import { getPdfPageInfo } from '../../utils/pdfUtils';
import { splitPdf } from '../../utils/pdfToolsUtils';
import { SelectedFile } from './types';
import ResultView from './ResultView';
import ProcessingOverlay from './ProcessingOverlay';
import { font, space, radius } from '../../styles/theme';

interface SplitToolProps {
  onBack: () => void;
  onPickFile: () => Promise<SelectedFile[]>;
  saveRecent?: (name: string, path: string, pages: number, tool: 'split') => void;
  onSendToPc?: (filePath: string) => void;
}

export default function SplitTool({ onBack, onPickFile, saveRecent, onSendToPc }: SplitToolProps) {
  const { colors, shadows } = useAppTheme();
  const s = useMemo(() => createPdfToolsStyles(colors, shadows), [colors, shadows]);
  
  const ls = useMemo(() => StyleSheet.create({
    grid: {
      flexDirection: 'row',
      flexWrap: 'wrap',
      gap: space.sm,
      marginTop: space.md,
      paddingBottom: space.xl,
    },
    pageWrap: {
      width: '23%',
      alignItems: 'center',
      marginBottom: space.md,
      position: 'relative'
    },
    thumbWrap: {
      width: 70,
      height: 100,
      borderRadius: radius.sm,
      backgroundColor: colors.bg.elevated,
      overflow: 'hidden',
      borderWidth: 2,
    },
    thumb: {
      width: '100%',
      height: '100%',
    },
    pageBadge: {
      position: 'absolute',
      bottom: -10,
      backgroundColor: colors.bg.elevated,
      paddingHorizontal: 6,
      paddingVertical: 2,
      borderRadius: radius.pill,
      borderWidth: 1,
      borderColor: colors.border.subtle,
      zIndex: 2,
    },
    pageBadgeText: {
      fontFamily: font.medium,
      fontSize: 10,
      color: colors.text.secondary
    },
    splitPoint: {
      position: 'absolute',
      right: -10,
      top: '50%',
      height: 24,
      width: 24,
      borderRadius: 12,
      backgroundColor: colors.accent.primary,
      alignItems: 'center',
      justifyContent: 'center',
      zIndex: 3,
      transform: [{ translateY: -12 }],
    },
    presetRow: {
      flexDirection: 'row',
      flexWrap: 'wrap',
      gap: space.sm,
      marginTop: space.sm
    },
    presetChip: {
      backgroundColor: colors.bg.elevated,
      paddingHorizontal: space.md,
      paddingVertical: space.sm,
      borderRadius: radius.md,
      borderWidth: 1,
      borderColor: colors.border.subtle
    },
    presetChipText: {
      fontFamily: font.medium,
      fontSize: 12,
      color: colors.text.primary
    },
    sectionTitle: {
      fontFamily: font.semibold,
      fontSize: 14,
      color: colors.text.primary,
      marginTop: space.lg,
      marginBottom: space.sm
    },
    outPreview: {
      backgroundColor: colors.bg.elevated,
      padding: space.md,
      borderRadius: radius.md,
      marginTop: space.lg
    }
  }), [colors]);

  const [file, setFile] = useState<SelectedFile | null>(null);
  const [pageCount, setPageCount] = useState(0);
  const [splitPoints, setSplitPoints] = useState<Set<number>>(new Set());
  const [baseName, setBaseName] = useState('Part');
  
  const [thumbnails, setThumbnails] = useState<Record<number, string>>({});
  const [loadingThumbs, setLoadingThumbs] = useState(false);

  const [loading, setLoading] = useState(false);
  const [resultPaths, setResultPaths] = useState<string[]>([]);

  const handlePick = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const files = await onPickFile();
    if (files.length) {
      setFile(files[0]);
      setBaseName(files[0].name.replace(/\.pdf$/i, '') + '_Part');
      setThumbnails({});
      setSplitPoints(new Set());
      setLoadingThumbs(true);
      try {
        const info = await getPdfPageInfo(files[0].uri);
        setPageCount(info.pageCount);
        generateThumbnails(files[0].uri, info.pageCount);
      } catch (e: any) {
        Alert.alert('Error', 'Failed to read PDF info');
        setLoadingThumbs(false);
      }
    }
  };

  const generateThumbnails = async (uri: string, total: number) => {
    let current = 0;
    const batchSize = 5;
    while (current < total) {
      const batch = Array.from({ length: Math.min(batchSize, total - current) }, (_, i) => current + i);
      const newThumbs: Record<number, string> = {};
      for (const p of batch) {
        try {
          const res = await PdfThumbnail.generate(uri, p, 50);
          newThumbs[p + 1] = res.uri;
        } catch (e) {}
      }
      setThumbnails(prev => ({ ...prev, ...newThumbs }));
      current += batchSize;
    }
    setLoadingThumbs(false);
  };

  const toggleSplitPoint = (pageIndex: number) => {
    if (pageIndex >= pageCount) return; // Can't split after the last page
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    setSplitPoints(prev => {
      const next = new Set(prev);
      if (next.has(pageIndex)) next.delete(pageIndex);
      else next.add(pageIndex);
      return next;
    });
  };

  // Grouping logic for colors
  const groupColors = [
    colors.accent.primaryDim,
    colors.accent.successDim,
    colors.accent.warningDim,
    colors.accent.errorDim,
    'rgba(156, 39, 176, 0.14)',
  ];

  const getPageGroup = (pageNum: number) => {
    let group = 0;
    for (let i = 1; i < pageNum; i++) {
      if (splitPoints.has(i)) group++;
    }
    return group;
  };

  const applyPreset = (type: string) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const newPoints = new Set<number>();
    
    if (type === 'every') {
      for (let i = 1; i < pageCount; i++) newPoints.add(i);
    } else if (type === 'half') {
      if (pageCount > 1) newPoints.add(Math.ceil(pageCount / 2));
    } else if (type === 'every2') {
      for (let i = 2; i < pageCount; i += 2) newPoints.add(i);
    } else if (type === 'every5') {
      for (let i = 5; i < pageCount; i += 5) newPoints.add(i);
    }
    setSplitPoints(newPoints);
  };

  const handleSplit = async () => {
    if (!file) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);

    // Compute ranges from split points
    const sortedPoints = Array.from(splitPoints).sort((a, b) => a - b);
    const splitRanges: {start: number, end: number}[] = [];
    let start = 1;
    for (const p of sortedPoints) {
      splitRanges.push({ start, end: p });
      start = p + 1;
    }
    splitRanges.push({ start, end: pageCount });

    setLoading(true);
    try {
      // Assuming splitPdf can take custom names or we handle it in utils. 
      // For now, passing ranges works and utils generate names. 
      // Base name enhancement could be passed to splitPdf, but we stick to existing API signature if not changed.
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
        <ResultView paths={resultPaths} onDone={onBack} onSendToPc={onSendToPc} />
      </View>
    );
  }

  const outCount = splitPoints.size + 1;

  return (
    <View style={s.modalOverlay}>
      <ProcessingOverlay visible={loading} text="Splitting PDF…" />
      <View style={s.modalHeader}>
        <Pressable style={s.backBtn} onPress={onBack} accessibilityRole="button" accessibilityLabel="Go back"><Ionicons name="arrow-back" size={24} color={colors.text.primary} /></Pressable>
        <Text style={s.modalTitle}>Split PDF</Text>
      </View>
      <ScrollView style={s.modalScroll} contentContainerStyle={s.pb100}>
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

            {pageCount > 0 && (
              <>
                <Text style={ls.sectionTitle}>Presets</Text>
                <View style={ls.presetRow}>
                  <Pressable style={ls.presetChip} onPress={() => applyPreset('every')}><Text style={ls.presetChipText}>Every Page</Text></Pressable>
                  <Pressable style={ls.presetChip} onPress={() => applyPreset('half')} disabled={pageCount <= 1}><Text style={[ls.presetChipText, pageCount <= 1 && {color: colors.text.disabled}]}>First / Second Half</Text></Pressable>
                  <Pressable style={ls.presetChip} onPress={() => applyPreset('every2')}><Text style={ls.presetChipText}>Every 2 Pages</Text></Pressable>
                  <Pressable style={ls.presetChip} onPress={() => applyPreset('every5')}><Text style={ls.presetChipText}>Every 5 Pages</Text></Pressable>
                </View>

                <Text style={ls.sectionTitle}>Tap pages to insert split points</Text>
                <View style={ls.grid}>
                  {Array.from({ length: pageCount }).map((_, i) => {
                    const pageNum = i + 1;
                    const group = getPageGroup(pageNum);
                    const borderColor = groupColors[group % groupColors.length].replace('0.14', '0.6');
                    const bgColor = groupColors[group % groupColors.length];
                    const hasSplit = splitPoints.has(pageNum);
                    
                    return (
                      <Pressable key={pageNum} style={ls.pageWrap} onPress={() => toggleSplitPoint(pageNum)}>
                        <View style={[ls.thumbWrap, { borderColor, backgroundColor: bgColor }]}>
                          {thumbnails[pageNum] ? (
                            <Image source={{ uri: thumbnails[pageNum] }} style={ls.thumb} resizeMode="cover" />
                          ) : (
                            <View style={{ flex: 1, alignItems: 'center', justifyContent: 'center' }}>
                              {loadingThumbs && <ActivityIndicator size="small" color={colors.accent.primary} />}
                            </View>
                          )}
                        </View>
                        <View style={ls.pageBadge}><Text style={ls.pageBadgeText}>{pageNum}</Text></View>
                        {hasSplit && (
                          <View style={ls.splitPoint}>
                            <Ionicons name="cut" size={14} color="#fff" />
                          </View>
                        )}
                      </Pressable>
                    );
                  })}
                </View>

                <View style={ls.outPreview}>
                  <Text style={[ls.sectionTitle, { marginTop: 0 }]}>Output Preview</Text>
                  <Text style={{ color: colors.text.secondary, fontFamily: font.regular, fontSize: 13 }}>
                    This will create <Text style={{ color: colors.text.primary, fontFamily: font.bold }}>{outCount}</Text> PDF files.
                  </Text>
                </View>
              </>
            )}
          </>
        )}
      </ScrollView>
      {file && (
        <View style={s.modalActions}>
          <Pressable style={s.btnPrimary} onPress={handleSplit} disabled={loading} accessibilityRole="button" accessibilityLabel="Split PDF">
            <Text style={s.btnPrimaryText}>Split into {outCount} PDFs</Text>
          </Pressable>
        </View>
      )}
    </View>
  );
}
