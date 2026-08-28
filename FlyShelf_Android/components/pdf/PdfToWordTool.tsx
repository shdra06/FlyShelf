import React, { useState, useEffect } from 'react';
import { View, Text, ScrollView, Pressable, Alert } from 'react-native';
import { Ionicons, MaterialCommunityIcons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { colors, space, radius } from '../../styles/theme';
import s from '../../styles/pdfToolsStyles';
import { convertPdfToDocx } from '../../utils/pdfToWordUtils';
import { getPdfPageInfo } from '../../utils/pdfUtils';
import { PairedDevice } from '../../utils/deviceTypes';
import { SelectedFile } from './types';
import ResultView from './ResultView';
import ProcessingOverlay from './ProcessingOverlay';

interface PdfToWordToolProps {
  onBack: () => void;
  onPickFile: () => Promise<SelectedFile[]>;
  saveRecent: (name: string, path: string, pages: number, tool: 'pdfToWord') => void;
}

export default function PdfToWordTool({ onBack, onPickFile, saveRecent }: PdfToWordToolProps) {
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
          <ResultView path={resultPath} onDone={onBack} />
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

            {hasPc ? (
              <View style={{ backgroundColor: '#06B6D4' + '20', borderRadius: radius.md, padding: space.md, marginTop: space.md, flexDirection: 'row', alignItems: 'center' }}>
                <Ionicons name="desktop-outline" size={24} color="#06B6D4" style={{ marginRight: 10 }} />
                <View style={{ flex: 1 }}>
                  <Text style={{ color: '#06B6D4', fontWeight: '700', fontSize: 13 }}>Cross-Device Synergy Active</Text>
                  <Text style={{ color: colors.text.secondary, fontSize: 11, marginTop: 2 }}>
                    Uses paired PC desktop OpenXML engine for layout fidelity
                  </Text>
                </View>
              </View>
            ) : null}

            <View style={{ backgroundColor: colors.bg.card, borderRadius: radius.md, padding: space.lg, marginTop: space.md, borderWidth: 1, borderColor: colors.border.subtle }}>
              <Text style={{ color: colors.text.primary, fontWeight: '600', marginBottom: 8, fontSize: 14 }}>
                📄 Conversion Features:
              </Text>
              <Text style={{ color: colors.text.secondary, fontSize: 13, lineHeight: 20 }}>
                • Paragraph &amp; heading layout preservation{'\n'}
                • Multi-column table detection &amp; cell styling{'\n'}
                • Embedded image extraction &amp; document bundling{'\n'}
                • Compatible with Microsoft Word, LibreOffice, &amp; Google Docs
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
