import { useAppTheme } from '../../hooks/useAppTheme';
import React, { useState, useMemo } from 'react';
import { View, Text, ScrollView, Pressable, Alert } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as FileSystem from 'expo-file-system/legacy';
import { space, radius } from '../../styles/theme';
import { createPdfToolsStyles } from '../../styles/pdfToolsStyles';
import { compressPdf } from '../../utils/pdfToolsUtils';
import { getPdfPageInfo } from '../../utils/pdfUtils';
import { SelectedFile } from './types';
import ResultView from './ResultView';
import ProcessingOverlay from './ProcessingOverlay';

interface CompressToolProps {
  onBack: () => void;
  onPickFile: () => Promise<SelectedFile[]>;
  saveRecent: (name: string, path: string, pages: number, tool: 'compress') => void;
  onSendToPc?: (filePath: string) => void;
}

export default function CompressTool({ onBack, onPickFile, saveRecent, onSendToPc }: CompressToolProps) {
  const { colors, shadows } = useAppTheme();
  const s = useMemo(() => createPdfToolsStyles(colors, shadows), [colors, shadows]);

  const [file, setFile] = useState<SelectedFile | null>(null);
  const [pageCount, setPageCount] = useState(0);
  const [origSize, setOrigSize] = useState<number | null>(null);
  const [compressedSize, setCompressedSize] = useState<number | null>(null);
  const [loading, setLoading] = useState(false);
  const [resultPath, setResultPath] = useState<string | null>(null);
  const [quality, setQuality] = useState<'low' | 'medium' | 'high'>('medium');

  const COMPRESSION_LEVELS = [
    { id: 'low', label: 'Low', quality: 'low', icon: 'flash-outline' as const, color: colors.accent.success, desc: 'Smallest file' },
    { id: 'medium', label: 'Medium', quality: 'medium', icon: 'speedometer-outline' as const, color: colors.accent.primary, desc: 'Balanced' },
    { id: 'high', label: 'High', quality: 'high', icon: 'diamond-outline' as const, color: colors.accent.warning, desc: 'Best quality' },
  ];

  const handlePick = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const files = await onPickFile();
    if (files.length) {
      setFile(files[0]);
      setOrigSize(files[0].size ?? null);
      try {
        const info = await getPdfPageInfo(files[0].uri);
        setPageCount(info.pageCount);
      } catch {
        // Page count fallback
      }
    }
  };

  const handleCompress = async () => {
    if (!file) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setLoading(true);
    try {
      const outPath = await compressPdf(file.uri, quality);
      const info = await FileSystem.getInfoAsync(outPath);
      if (info.exists && 'size' in info && typeof info.size === 'number') {
        setCompressedSize(info.size);
      }
      setResultPath(outPath);
      saveRecent(file.name, outPath, pageCount, 'compress');
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    } catch (e: any) {
      Alert.alert('Compression Failed', e.message || 'Unable to compress PDF.');
    } finally {
      setLoading(false);
    }
  };

  const formatSize = (bytes: number | null) => {
    if (bytes === null || bytes === undefined) return 'Unknown';
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
  };

  if (resultPath) {
    const savedPercent = origSize && compressedSize && origSize > compressedSize
      ? Math.round(((origSize - compressedSize) / origSize) * 100)
      : null;
    const gotLarger = origSize && compressedSize && compressedSize >= origSize;

    return (
      <View style={s.modalOverlay}>
        <View style={s.modalHeader}>
          <Pressable style={s.backBtn} onPress={onBack} accessibilityRole="button" accessibilityLabel="Go back">
            <Ionicons name="arrow-back" size={24} color={colors.text.primary} />
          </Pressable>
          <Text style={s.modalTitle}>Compression Complete</Text>
        </View>
        <ScrollView style={s.modalScroll} contentContainerStyle={{ paddingBottom: 40 }}>
          {savedPercent !== null && (
            <View style={{ backgroundColor: colors.accent.successDim, padding: space.lg, borderRadius: radius.md, marginBottom: space.lg, alignItems: 'center' }}>
              <Text style={{ color: colors.accent.success, fontSize: 18, fontWeight: '700' }}>
                🎉 Reduced by {savedPercent}%!
              </Text>
              <Text style={{ color: colors.text.secondary, marginTop: 4, fontSize: 13 }}>
                {formatSize(origSize)} → {formatSize(compressedSize)}
              </Text>
            </View>
          )}
          {gotLarger && (
            <View style={{ backgroundColor: colors.accent.warningDim, padding: space.lg, borderRadius: radius.md, marginBottom: space.lg, alignItems: 'center' }}>
              <Text style={{ color: colors.accent.warning, fontSize: 16, fontWeight: '700' }}>
                ⚠️ File Got Larger
              </Text>
              <Text style={{ color: colors.text.secondary, marginTop: 4, fontSize: 13, textAlign: 'center' }}>
                This PDF was already highly optimized or didn't contain compressible elements like large images.
              </Text>
              <Text style={{ color: colors.text.secondary, marginTop: 4, fontSize: 13 }}>
                {formatSize(origSize)} → {formatSize(compressedSize)}
              </Text>
            </View>
          )}
          <ResultView path={resultPath} onDone={onBack} onSendToPc={onSendToPc} />
        </ScrollView>
      </View>
    );
  }

  return (
    <View style={s.modalOverlay}>
      <ProcessingOverlay visible={loading} text="Optimizing and compressing PDF…" />
      <View style={s.modalHeader}>
        <Pressable style={s.backBtn} onPress={onBack} accessibilityRole="button" accessibilityLabel="Go back">
          <Ionicons name="arrow-back" size={24} color={colors.text.primary} />
        </Pressable>
        <Text style={s.modalTitle}>Compress PDF</Text>
      </View>

      <ScrollView style={s.modalScroll} contentContainerStyle={{ paddingBottom: 40 }}>
        {!file ? (
          <Pressable style={[s.fileItem, { paddingVertical: 28, justifyContent: 'center', alignItems: 'center', flexDirection: 'column' }]} onPress={handlePick}>
            <Ionicons name="cloud-upload-outline" size={36} color={colors.accent.primary} />
            <Text style={[s.fileName, { marginTop: 8, fontSize: 15 }]}>Select PDF to Compress</Text>
            <Text style={{ color: colors.text.tertiary, fontSize: 12, marginTop: 4 }}>
              Reduces file size while maintaining quality
            </Text>
          </Pressable>
        ) : (
          <View>
            <View style={s.fileItem}>
              <Ionicons name="document-text" size={28} color={colors.type.pdf} style={s.fileIcon} />
              <View style={s.fileInfo}>
                <Text style={s.fileName} numberOfLines={1}>{file.name}</Text>
                <Text style={s.fileMeta}>
                  {formatSize(origSize)} • {pageCount > 0 ? `${pageCount} pages` : 'PDF'}
                </Text>
              </View>
              <Pressable onPress={handlePick} style={{ padding: 6 }}>
                <Ionicons name="swap-horizontal" size={20} color={colors.accent.primary} />
              </Pressable>
            </View>

            <View style={{ flexDirection: 'row', gap: space.md, marginTop: space.lg }}>
              {COMPRESSION_LEVELS.map(level => (
                <Pressable
                  key={level.id}
                  onPress={() => { setQuality(level.quality as any); Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); }}
                  style={{
                    flex: 1,
                    backgroundColor: colors.bg.card,
                    borderRadius: radius.md,
                    padding: space.md,
                    alignItems: 'center',
                    borderWidth: 2,
                    borderColor: quality === level.quality ? level.color : colors.border.subtle,
                  }}
                >
                  <Ionicons name={level.icon} size={24} color={level.color} style={{ marginBottom: 4 }} />
                  <Text style={{ color: colors.text.primary, fontWeight: '600', fontSize: 13 }}>{level.label}</Text>
                  <Text style={{ color: colors.text.tertiary, fontSize: 11, marginTop: 2 }}>{level.desc}</Text>
                </Pressable>
              ))}
            </View>

            <View style={{ backgroundColor: colors.bg.card, borderRadius: radius.md, padding: space.lg, marginTop: space.xl, borderWidth: 1, borderColor: colors.border.subtle }}>
              <Text style={{ color: colors.text.primary, fontWeight: '600', marginBottom: 8, fontSize: 14 }}>
                ⚡ Optimization Features:
              </Text>
              <Text style={{ color: colors.text.secondary, fontSize: 13, lineHeight: 20 }}>
                • Flate compression stream re-encoding{'\n'}
                • Strips unreferenced objects & metadata bloat{'\n'}
                • Cleans redundant font descriptors
              </Text>
            </View>

            <Pressable style={[s.btnPrimary, { marginTop: space.xl }]} onPress={handleCompress}>
              <Text style={s.btnPrimaryText}>Compress PDF Now ⚡</Text>
            </Pressable>
          </View>
        )}
      </ScrollView>
    </View>
  );
}
