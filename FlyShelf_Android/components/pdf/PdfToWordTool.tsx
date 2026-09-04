import { useAppTheme } from '../../hooks/useAppTheme';
import React, { useState, useEffect, useMemo } from 'react';
import { View, Text, ScrollView, Pressable, Alert } from 'react-native';
import { Ionicons, MaterialCommunityIcons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { space, radius } from '../../styles/theme';
import { createPdfToolsStyles } from '../../styles/pdfToolsStyles';
import { convertPdfToDocx } from '../../utils/pdfToWordUtils';
import { getPdfPageInfo } from '../../utils/pdfUtils';
import { PairedDevice } from '../../utils/deviceTypes';
import { SelectedFile } from './types';
import ResultView from './ResultView';
import ProcessingOverlay from './ProcessingOverlay';
import { useSettings } from '../../context/SettingsContext';

interface PdfToWordToolProps {
  onBack: () => void;
  onPickFile: () => Promise<SelectedFile[]>;
  saveRecent: (name: string, path: string, pages: number, tool: 'pdfToWord') => void;
  onSendToPc?: (filePath: string) => void;
}

export default function PdfToWordTool({ onBack, onPickFile, saveRecent, onSendToPc }: PdfToWordToolProps) {
  const { colors, shadows } = useAppTheme();
  const s = useMemo(() => createPdfToolsStyles(colors, shadows), [colors, shadows]);

  const [file, setFile] = useState<SelectedFile | null>(null);
  const [pageCount, setPageCount] = useState(0);
  const [hasPc, setHasPc] = useState(false);
  const [loading, setLoading] = useState(false);
  const [resultPath, setResultPath] = useState<string | null>(null);
  const [conversionMethod, setConversionMethod] = useState<'pc_accelerated' | 'client_openxml' | null>(null);

  useEffect(() => {
    AsyncStorage.getItem('@flyshelf_paired_devices').then(data => {
      if (data) {
        const devices: PairedDevice[] = JSON.parse(data);
        const pc = devices.some((d: PairedDevice) => d.deviceType === 'PC');
        setHasPc(pc);
      }
    }).catch(() => {});
  }, []);

  const handlePick = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const files = await onPickFile();
    if (files.length) {
      setFile(files[0]);
      try {
        const info = await getPdfPageInfo(files[0].uri);
        setPageCount(info.pageCount);
      } catch { }
    }
  };

  const handleConvert = async () => {
    if (!file) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setLoading(true);
    try {
      const { docxPath, method } = await convertPdfToDocx(file.uri, file.name);
      setResultPath(docxPath);
      setConversionMethod(method);
      saveRecent(file.name.replace(/\.pdf$/i, '.docx'), docxPath, pageCount, 'pdfToWord');
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    } catch (e: any) {
      Alert.alert('Conversion Failed', e.message || 'Unable to convert PDF to Word document.');
    } finally {
      setLoading(false);
    }
  };

  if (resultPath) {
    return (
      <View style={s.modalOverlay}>
        <View style={s.modalHeader}>
          <Pressable style={s.backBtn} onPress={onBack} accessibilityRole="button" accessibilityLabel="Go back">
            <Ionicons name="arrow-back" size={24} color={colors.text.primary} />
          </Pressable>
          <Text style={s.modalTitle}>Word Document Ready</Text>
        </View>
        <ScrollView style={s.modalScroll} contentContainerStyle={{ paddingBottom: 40 }}>
          <View style={{ backgroundColor: colors.accent.successDim, padding: space.lg, borderRadius: radius.md, marginBottom: space.lg, alignItems: 'center' }}>
            <MaterialCommunityIcons name="file-word-box" size={40} color={colors.type.doc} />
            <Text style={{ color: colors.accent.success, fontSize: 18, fontWeight: '700', marginTop: 8 }}>
              DOCX Created Successfully!
            </Text>
            <Text style={{ color: colors.text.secondary, marginTop: 4, fontSize: 13, textAlign: 'center' }}>
              {conversionMethod === 'pc_accelerated'
                ? '⚡ Rendered by Paired PC OpenXML Engine'
                : '📄 Generated via Standalone OpenXML Converter'}
            </Text>
          </View>
          <ResultView path={resultPath} onDone={onBack} onSendToPc={onSendToPc} />
        </ScrollView>
      </View>
    );
  }

  return (
    <View style={s.modalOverlay}>
      <ProcessingOverlay
        visible={loading}
        text={hasPc ? 'Accelerating with Paired PC OpenXML engine…' : 'Generating Word (.docx) document…'}
      />
      <View style={s.modalHeader}>
        <Pressable style={s.backBtn} onPress={onBack} accessibilityRole="button" accessibilityLabel="Go back">
          <Ionicons name="arrow-back" size={24} color={colors.text.primary} />
        </Pressable>
        <Text style={s.modalTitle}>PDF to Word (.docx)</Text>
      </View>

      <ScrollView style={s.modalScroll} contentContainerStyle={{ paddingBottom: 40 }}>
        {!hasPc ? (
          <View style={{ backgroundColor: colors.accent.warningDim, padding: space.md, borderRadius: radius.md, marginBottom: space.md, borderWidth: 1, borderColor: colors.accent.warning }}>
            <Text style={{ color: colors.accent.warning, fontSize: 14, fontWeight: '600' }}>⚠️ Standalone Mode</Text>
            <Text style={{ color: colors.text.secondary, fontSize: 13, marginTop: 4 }}>
              Basic conversion only — text layout and tables require a paired PC for full accuracy.
            </Text>
          </View>
        ) : (
          <View style={{ backgroundColor: colors.accent.successDim, padding: space.md, borderRadius: radius.md, marginBottom: space.md, borderWidth: 1, borderColor: colors.accent.success }}>
            <Text style={{ color: colors.accent.success, fontSize: 14, fontWeight: '600' }}>✓ Full Conversion Available</Text>
            <Text style={{ color: colors.text.secondary, fontSize: 13, marginTop: 4 }}>
              Paragraph layout, tables, and formatting will be preserved via desktop engine.
            </Text>
          </View>
        )}

        {!file ? (
          <Pressable
            style={[s.fileItem, { paddingVertical: 28, justifyContent: 'center', alignItems: 'center', flexDirection: 'column' }]}
            onPress={handlePick}
          >
            <MaterialCommunityIcons name="file-word-outline" size={40} color={colors.type.doc} />
            <Text style={[s.fileName, { marginTop: 8, fontSize: 15 }]}>Select PDF to Convert</Text>
            <Text style={{ color: colors.text.tertiary, fontSize: 12, marginTop: 4 }}>
              Outputs formatted editable Microsoft Word (.docx)
            </Text>
          </Pressable>
        ) : (
          <View>
            <View style={s.fileItem}>
              <Ionicons name="document-text" size={28} color={colors.type.pdf} style={s.fileIcon} />
              <View style={s.fileInfo}>
                <Text style={s.fileName} numberOfLines={1}>{file.name}</Text>
                <Text style={s.fileMeta}>
                  {file.size ? `${(file.size / 1024 / 1024).toFixed(2)} MB` : 'PDF'} • {pageCount > 0 ? `${pageCount} pages` : 'Document'}
                </Text>
              </View>
              <Pressable onPress={handlePick} style={{ padding: 6 }}>
                <Ionicons name="swap-horizontal" size={20} color={colors.accent.primary} />
              </Pressable>
            </View>

            <View style={{ backgroundColor: colors.bg.card, borderRadius: radius.md, padding: space.lg, marginTop: space.md, borderWidth: 1, borderColor: colors.border.subtle }}>
              <Text style={{ color: colors.text.primary, fontWeight: '600', marginBottom: 8, fontSize: 14 }}>
                📄 Conversion Features:
              </Text>
              <Text style={{ color: colors.text.secondary, fontSize: 13, lineHeight: 20 }}>
                {hasPc ? (
                  <>
                    • Full paragraph layout{'\n'}
                    • Table detection{'\n'}
                    • Image extraction
                  </>
                ) : (
                  <>
                    • Page structure export{'\n'}
                    • Basic text flow
                  </>
                )}
              </Text>
            </View>

            <Pressable style={[s.btnPrimary, { marginTop: space.xl, backgroundColor: colors.type.doc }]} onPress={handleConvert}>
              <Text style={s.btnPrimaryText}>Convert to Word (.docx)</Text>
            </Pressable>
          </View>
        )}
      </ScrollView>
    </View>
  );
}
