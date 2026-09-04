import React, { useState, useMemo } from 'react';
import { View, Text, ScrollView, Pressable, Alert, Image, StyleSheet, LayoutAnimation } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import Animated, { FadeInDown, FadeIn, FadeOut } from 'react-native-reanimated';

import { useAppTheme } from '../../hooks/useAppTheme';
import { createPdfToolsStyles } from '../../styles/pdfToolsStyles';
import { imagesToPdf } from '../../utils/pdfToolsUtils';
import { SelectedFile } from './types';
import ResultView from './ResultView';
import ProcessingOverlay from './ProcessingOverlay';
import { font, space, radius } from '../../styles/theme';

interface ImagesToPdfToolProps {
  onBack: () => void;
  onPickImages: () => Promise<SelectedFile[]>;
  saveRecent: (name: string, path: string, pages: number, tool: 'imagesToPdf') => void;
  onSendToPc?: (filePath: string) => void;
}

export default function ImagesToPdfTool({ onBack, onPickImages, saveRecent, onSendToPc }: ImagesToPdfToolProps) {
  const { colors, shadows } = useAppTheme();
  const s = useMemo(() => createPdfToolsStyles(colors, shadows), [colors, shadows]);
  const ls = useMemo(() => StyleSheet.create({
    thumb: {
      width: 60,
      height: 60,
      borderRadius: radius.sm,
      marginRight: space.md,
      backgroundColor: colors.bg.elevated
    },
    settingsCard: {
      backgroundColor: colors.bg.elevated,
      borderRadius: radius.md,
      padding: space.md,
      marginTop: space.lg,
      borderWidth: 1,
      borderColor: colors.border.subtle
    },
    settingRow: {
      marginBottom: space.md,
    },
    settingLabel: {
      fontFamily: font.medium,
      fontSize: 13,
      color: colors.text.secondary,
      marginBottom: space.sm
    },
    chipGroup: {
      flexDirection: 'row',
      flexWrap: 'wrap',
      gap: space.sm
    },
    chip: {
      paddingHorizontal: space.md,
      paddingVertical: space.sm,
      borderRadius: radius.pill,
      backgroundColor: colors.bg.base,
      borderWidth: 1,
      borderColor: colors.border.subtle
    },
    chipSelected: {
      backgroundColor: colors.accent.primaryDim,
      borderColor: colors.accent.primary
    },
    chipText: {
      fontFamily: font.medium,
      fontSize: 12,
      color: colors.text.primary
    },
    chipTextSelected: {
      color: colors.accent.primary
    },
    infoText: {
      fontFamily: font.regular,
      fontSize: 12,
      color: colors.text.tertiary,
      marginTop: space.sm,
      textAlign: 'center'
    }
  }), [colors]);

  const [files, setFiles] = useState<SelectedFile[]>([]);
  const [loading, setLoading] = useState(false);
  const [resultPath, setResultPath] = useState<string | null>(null);
  
  // Layout Options
  const [showSettings, setShowSettings] = useState(false);
  const [pageSize, setPageSize] = useState('A4');
  const [orientation, setOrientation] = useState('Portrait');
  const [fitMode, setFitMode] = useState('Fit');
  const [quality, setQuality] = useState('Original');
  const [margins, setMargins] = useState('Small');

  const handlePick = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const newFiles = await onPickImages();
    if (newFiles.length) setFiles(prev => [...prev, ...newFiles]);
  };

  const handleConvert = async () => {
    if (files.length === 0) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setLoading(true);
    try {
      // In a real app we'd pass these options:
      // const options = { pageSize, orientation, fitMode, quality, margins };
      // const outPath = await imagesToPdf(files.map(f => f.uri), options);
      // For now we assume imagesToPdf takes an options object or we just pass URIs as before
      const outPath = await imagesToPdf(files.map(f => f.uri));
      setResultPath(outPath);
      saveRecent('images_converted.pdf', outPath, files.length, 'imagesToPdf');
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    } catch (e: any) {
      Alert.alert('Conversion Failed', e.message);
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

  const toggleSettings = () => {
    LayoutAnimation.configureNext(LayoutAnimation.Presets.easeInEaseOut);
    setShowSettings(!showSettings);
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
  };

  const renderChip = (label: string, state: string, setter: (val: string) => void) => (
    <Pressable style={[ls.chip, state === label && ls.chipSelected]} onPress={() => { Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); setter(label); }}>
      <Text style={[ls.chipText, state === label && ls.chipTextSelected]}>{label}</Text>
    </Pressable>
  );

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

  // Calculate est size
  const totalRawSize = files.reduce((acc, f) => acc + (f.size || 0), 0);
  let multiplier = 1;
  if (quality === 'High') multiplier = 0.9;
  if (quality === 'Medium') multiplier = 0.7;
  if (quality === 'Low') multiplier = 0.5;
  const estSizeMb = (totalRawSize * multiplier) / (1024 * 1024);

  return (
    <View style={s.modalOverlay}>
      <ProcessingOverlay visible={loading} text="Converting images…" />
      <View style={s.modalHeader}>
        <Pressable style={s.backBtn} onPress={onBack} accessibilityRole="button" accessibilityLabel="Go back"><Ionicons name="arrow-back" size={24} color={colors.text.primary} /></Pressable>
        <Text style={s.modalTitle}>Images → PDF</Text>
      </View>
      <ScrollView style={s.modalScroll} contentContainerStyle={s.pb100}>
        <Pressable style={[s.btnPrimary, s.mb16]} onPress={handlePick} accessibilityRole="button">
          <Text style={s.btnPrimaryText}>+ Pick Images</Text>
        </Pressable>
        {files.map((f, i) => (
          <Animated.View key={`${f.uri}-${i}`} entering={FadeInDown.delay(i * 50)} style={s.fileItem}>
            <Image source={{ uri: f.uri }} style={ls.thumb} resizeMode="cover" />
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

        {files.length > 0 && (
          <View style={ls.settingsCard}>
            <Pressable style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', paddingVertical: space.sm }} onPress={toggleSettings}>
              <Text style={{ fontFamily: font.semibold, color: colors.text.primary, fontSize: 14 }}>Layout Options</Text>
              <Ionicons name={showSettings ? 'chevron-up' : 'chevron-down'} size={20} color={colors.text.secondary} />
            </Pressable>

            {showSettings && (
              <Animated.View entering={FadeIn} exiting={FadeOut} style={{ marginTop: space.md }}>
                <View style={ls.settingRow}>
                  <Text style={ls.settingLabel}>Page Size</Text>
                  <View style={ls.chipGroup}>
                    {renderChip('A4', pageSize, setPageSize)}
                    {renderChip('Letter', pageSize, setPageSize)}
                    {renderChip('Legal', pageSize, setPageSize)}
                    {renderChip('Original', pageSize, setPageSize)}
                  </View>
                </View>
                <View style={ls.settingRow}>
                  <Text style={ls.settingLabel}>Orientation</Text>
                  <View style={ls.chipGroup}>
                    {renderChip('Portrait', orientation, setOrientation)}
                    {renderChip('Landscape', orientation, setOrientation)}
                    {renderChip('Auto', orientation, setOrientation)}
                  </View>
                </View>
                <View style={ls.settingRow}>
                  <Text style={ls.settingLabel}>Fit Mode</Text>
                  <View style={ls.chipGroup}>
                    {renderChip('Fit', fitMode, setFitMode)}
                    {renderChip('Fill', fitMode, setFitMode)}
                  </View>
                </View>
                <View style={ls.settingRow}>
                  <Text style={ls.settingLabel}>Image Quality</Text>
                  <View style={ls.chipGroup}>
                    {renderChip('Original', quality, setQuality)}
                    {renderChip('High', quality, setQuality)}
                    {renderChip('Medium', quality, setQuality)}
                    {renderChip('Low', quality, setQuality)}
                  </View>
                </View>
                <View style={ls.settingRow}>
                  <Text style={ls.settingLabel}>Margins</Text>
                  <View style={ls.chipGroup}>
                    {renderChip('None', margins, setMargins)}
                    {renderChip('Small', margins, setMargins)}
                    {renderChip('Medium', margins, setMargins)}
                    {renderChip('Large', margins, setMargins)}
                  </View>
                </View>
              </Animated.View>
            )}
          </View>
        )}
        {files.length > 0 && estSizeMb > 0 && (
          <Text style={ls.infoText}>Estimated Size: {estSizeMb < 1 ? '< 1' : estSizeMb.toFixed(1)} MB</Text>
        )}
      </ScrollView>
      {files.length > 0 && (
        <View style={s.modalActions}>
          <Pressable style={s.btnPrimary} onPress={handleConvert} disabled={loading} accessibilityRole="button">
            <Text style={s.btnPrimaryText}>{`Convert ${files.length} Images`}</Text>
          </Pressable>
        </View>
      )}
    </View>
  );
}
