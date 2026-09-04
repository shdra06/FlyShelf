import React, { useState, useMemo, useEffect } from 'react';
import { View, Text, ScrollView, Pressable, Alert, ActivityIndicator, Image, StyleSheet, TextInput } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as FileSystem from 'expo-file-system/legacy';
import Animated, { FadeInDown } from 'react-native-reanimated';
import PdfThumbnail from 'react-native-pdf-thumbnail';

import { useAppTheme } from '../../hooks/useAppTheme';
import { createPdfToolsStyles } from '../../styles/pdfToolsStyles';
import { getPdfPageInfo, extractPages } from '../../utils/pdfUtils';
import { SelectedFile, PageEntry } from './types';
import ResultView from './ResultView';
import ProcessingOverlay from './ProcessingOverlay';
import { font, space, radius } from '../../styles/theme';

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
  const ls = useMemo(() => StyleSheet.create({
    grid: {
      flexDirection: 'row',
      flexWrap: 'wrap',
      gap: space.md,
      marginTop: space.sm,
    },
    thumbWrap: {
      width: '30%',
      aspectRatio: 1 / 1.414, // A4 ratio
      backgroundColor: colors.bg.elevated,
      borderRadius: radius.md,
      overflow: 'hidden',
      borderWidth: 2,
      borderColor: 'transparent',
      position: 'relative'
    },
    thumbWrapSelected: {
      borderColor: colors.accent.primary,
    },
    thumb: {
      width: '100%',
      height: '100%'
    },
    checkBadge: {
      position: 'absolute',
      top: space.xs,
      left: space.xs,
      width: 20,
      height: 20,
      borderRadius: 10,
      backgroundColor: 'rgba(0,0,0,0.5)',
      borderWidth: 1,
      borderColor: '#fff',
      alignItems: 'center',
      justifyContent: 'center',
      zIndex: 2,
    },
    checkBadgeSelected: {
      backgroundColor: colors.accent.primary,
      borderColor: colors.accent.primary,
    },
    pageBadge: {
      position: 'absolute',
      bottom: space.xs,
      right: space.xs,
      backgroundColor: 'rgba(0,0,0,0.6)',
      paddingHorizontal: 6,
      paddingVertical: 2,
      borderRadius: radius.sm,
      zIndex: 2,
    },
    pageBadgeText: {
      fontFamily: font.medium,
      fontSize: 10,
      color: '#fff'
    },
    sectionTitle: {
      fontFamily: font.semibold,
      fontSize: 14,
      color: colors.text.primary,
      marginTop: space.lg,
      marginBottom: space.sm
    },
    orderStrip: {
      marginTop: space.md,
      paddingVertical: space.sm,
    },
    orderThumbWrap: {
      width: 50,
      aspectRatio: 1 / 1.414,
      backgroundColor: colors.bg.elevated,
      borderRadius: radius.sm,
      overflow: 'hidden',
      marginRight: space.sm,
      position: 'relative'
    },
    removeBtn: {
      position: 'absolute',
      top: 2,
      right: 2,
      backgroundColor: 'rgba(0,0,0,0.5)',
      borderRadius: 10,
      zIndex: 2,
    }
  }), [colors]);

  const [file, setFile] = useState<SelectedFile | null>(null);
  const [pages, setPages] = useState<PageEntry[]>([]);
  const [thumbnails, setThumbnails] = useState<Record<number, string>>({});
  const [loadingPages, setLoadingPages] = useState(false);
  const [loadingThumbs, setLoadingThumbs] = useState(false);
  
  const [selectedOrder, setSelectedOrder] = useState<number[]>([]);
  const [rangeInput, setRangeInput] = useState('');

  const [loading, setLoading] = useState(false);
  const [resultPath, setResultPath] = useState<string | null>(null);

  const handlePick = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const files = await onPickFile();
    if (files.length) {
      setFile(files[0]);
      setLoadingPages(true);
      setThumbnails({});
      setSelectedOrder([]);
      setRangeInput('');
      try {
        const info = await getPdfPageInfo(files[0].uri);
        setPages(info.pages.map((p, i) => ({ ...p, index: i, originalIndex: i, rotation: 0, selected: false, source: 'original' as const })));
        generateThumbnails(files[0].uri, info.pageCount);
      } catch (e: any) {
        Alert.alert('Error', 'Failed to load PDF');
      } finally {
        setLoadingPages(false);
      }
    }
  };

  const generateThumbnails = async (uri: string, total: number) => {
    setLoadingThumbs(true);
    let current = 0;
    const batchSize = 5;
    while (current < total) {
      const batch = Array.from({ length: Math.min(batchSize, total - current) }, (_, i) => current + i);
      const newThumbs: Record<number, string> = {};
      for (const p of batch) {
        try {
          const res = await PdfThumbnail.generate(uri, p, 70);
          newThumbs[p] = res.uri;
        } catch (e) {}
      }
      setThumbnails(prev => ({ ...prev, ...newThumbs }));
      current += batchSize;
    }
    setLoadingThumbs(false);
  };

  const togglePage = (idx: number) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const isSelected = selectedOrder.includes(idx);
    let newOrder = [...selectedOrder];
    if (isSelected) {
      newOrder = newOrder.filter(id => id !== idx);
    } else {
      newOrder.push(idx);
    }
    setSelectedOrder(newOrder);
    syncRangeInput(newOrder);
  };

  const removeSelectedPage = (idxToRemove: number) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const newOrder = selectedOrder.filter((_, i) => i !== idxToRemove);
    setSelectedOrder(newOrder);
    syncRangeInput(newOrder);
  };

  const allSelected = pages.length > 0 && selectedOrder.length === pages.length;

  const toggleSelectAll = () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    let newOrder: number[];
    if (allSelected) {
      newOrder = [];
    } else {
      newOrder = Array.from({ length: pages.length }, (_, i) => i);
    }
    setSelectedOrder(newOrder);
    syncRangeInput(newOrder);
  };

  const syncRangeInput = (order: number[]) => {
    if (order.length === 0) {
      setRangeInput('');
      return;
    }
    // simple sync for now: just comma separate the 1-based indices to preserve order
    setRangeInput(order.map(i => i + 1).join(', '));
  };

  const parseRangeInput = (val: string) => {
    setRangeInput(val);
    const parts = val.split(',').map(s => s.trim()).filter(Boolean);
    const newOrder: number[] = [];
    for (const p of parts) {
      if (p.includes('-')) {
        const [startStr, endStr] = p.split('-');
        const s = parseInt(startStr, 10);
        const e = parseInt(endStr, 10);
        if (!isNaN(s) && !isNaN(e) && s >= 1 && e <= pages.length && s <= e) {
          for (let i = s; i <= e; i++) {
            newOrder.push(i - 1);
          }
        }
      } else {
        const num = parseInt(p, 10);
        if (!isNaN(num) && num >= 1 && num <= pages.length) {
          newOrder.push(num - 1);
        }
      }
    }
    setSelectedOrder(newOrder);
  };

  const handleExtract = async () => {
    if (!file) return;
    if (!selectedOrder.length) {
      Alert.alert('Selection Required', 'Please select at least one page to extract.');
      return;
    }
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setLoading(true);
    try {
      await FileSystem.makeDirectoryAsync(OUTPUT_DIR, { intermediates: true }).catch(() => {});
      const outPath = `${OUTPUT_DIR}extracted_${Date.now()}.pdf`;
      const oneBasedSelected = selectedOrder.map(i => i + 1);
      await extractPages(file.uri, oneBasedSelected, outPath);
      setResultPath(outPath);
      saveRecent(file.name, outPath, selectedOrder.length, 'extract');
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
          <Pressable style={[s.btnPrimary, s.mb16]} onPress={handlePick} accessibilityRole="button"><Text style={s.btnPrimaryText}>Pick PDF</Text></Pressable>
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

            <View style={[s.inputRow, s.mt16, { justifyContent: 'space-between' }]}>
              <Text style={s.label}>{selectedOrder.length} of {pages.length} pages selected</Text>
              <Pressable style={s.btnSmall} onPress={toggleSelectAll}>
                <Text style={{ fontFamily: font.medium, fontSize: 12, color: colors.accent.primary }}>
                  {allSelected ? 'Deselect All' : 'Select All'}
                </Text>
              </Pressable>
            </View>

            <View style={ls.grid}>
              {pages.map((p, i) => {
                const isSelected = selectedOrder.includes(i);
                return (
                  <Pressable key={i} style={[ls.thumbWrap, isSelected && ls.thumbWrapSelected]} onPress={() => togglePage(i)}>
                    <View style={[ls.checkBadge, isSelected && ls.checkBadgeSelected]}>
                      {isSelected && <Ionicons name="checkmark" size={14} color="#fff" />}
                    </View>
                    {thumbnails[i] ? (
                      <Image source={{ uri: thumbnails[i] }} style={ls.thumb} resizeMode="cover" />
                    ) : (
                      <View style={{ flex: 1, alignItems: 'center', justifyContent: 'center' }}>
                         {loadingThumbs && <ActivityIndicator size="small" color={colors.accent.primary} />}
                      </View>
                    )}
                    <View style={ls.pageBadge}><Text style={ls.pageBadgeText}>{i + 1}</Text></View>
                  </Pressable>
                );
              })}
            </View>

            <Text style={ls.sectionTitle}>Range Input</Text>
            <TextInput
              style={s.input}
              value={rangeInput}
              onChangeText={parseRangeInput}
              placeholder="e.g. 1, 3-5, 8"
              placeholderTextColor={colors.text.tertiary}
            />

            {selectedOrder.length > 0 && (
              <>
                <Text style={ls.sectionTitle}>Extraction Order</Text>
                <ScrollView horizontal showsHorizontalScrollIndicator={false} style={ls.orderStrip}>
                  {selectedOrder.map((pageIdx, arrIdx) => (
                    <View key={`${pageIdx}-${arrIdx}`} style={ls.orderThumbWrap}>
                      <Pressable style={ls.removeBtn} onPress={() => removeSelectedPage(arrIdx)}>
                        <Ionicons name="close" size={12} color="#fff" />
                      </Pressable>
                      {thumbnails[pageIdx] ? (
                        <Image source={{ uri: thumbnails[pageIdx] }} style={ls.thumb} resizeMode="cover" />
                      ) : (
                        <View style={{ flex: 1, backgroundColor: colors.bg.base }} />
                      )}
                      <View style={ls.pageBadge}><Text style={ls.pageBadgeText}>{pageIdx + 1}</Text></View>
                    </View>
                  ))}
                </ScrollView>
              </>
            )}
          </>
        )}
      </ScrollView>
      {file && selectedOrder.length > 0 && !loadingPages && (
        <View style={s.modalActions}>
          <Pressable style={s.btnPrimary} onPress={handleExtract} accessibilityRole="button">
            <Text style={s.btnPrimaryText}>Extract {selectedOrder.length} Pages</Text>
          </Pressable>
        </View>
      )}
    </View>
  );
}
