import React, { useState } from 'react';
import { View, Text, ScrollView, Pressable, TextInput, Alert, ActivityIndicator } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import { colors } from '../../styles/theme';
import s from '../../styles/pdfToolsStyles';
import { addWatermark } from '../../utils/pdfToolsUtils';
import { getPdfPageInfo } from '../../utils/pdfUtils';
import { SelectedFile } from './types';
import ResultView from './ResultView';
import ProcessingOverlay from './ProcessingOverlay';

interface WatermarkToolProps {
  onBack: () => void;
  onPickFile: () => Promise<SelectedFile[]>;
  saveRecent: (name: string, path: string, pages: number, tool: 'watermark') => void;
}

/** Color preset for watermark */
interface ColorPreset {
  label: string;
  hex: string;
  value: { r: number; g: number; b: number };
}

const COLOR_PRESETS: ColorPreset[] = [
  { label: 'Gray',  hex: '#808080', value: { r: 0.5, g: 0.5, b: 0.5 } },
  { label: 'Red',   hex: '#CC1A1A', value: { r: 0.8, g: 0.1, b: 0.1 } },
  { label: 'Blue',  hex: '#1A33CC', value: { r: 0.1, g: 0.2, b: 0.8 } },
  { label: 'Black', hex: '#1A1A1A', value: { r: 0.1, g: 0.1, b: 0.1 } },
];

export default function WatermarkTool({ onBack, onPickFile, saveRecent }: WatermarkToolProps) {
  const [file, setFile] = useState<SelectedFile | null>(null);
  const [pageCount, setPageCount] = useState(0);
  const [text, setText] = useState('CONFIDENTIAL');
  const [opacity, setOpacity] = useState('0.15');
  const [fontSize, setFontSize] = useState('48');
  const [rotation, setRotation] = useState('-45');
  const [colorIdx, setColorIdx] = useState(0);
  const [loading, setLoading] = useState(false);
  const [resultPath, setResultPath] = useState<string | null>(null);

  const handlePick = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const files = await onPickFile();
    if (files.length) {
      setFile(files[0]);
      try {
        const info = await getPdfPageInfo(files[0].uri);
        setPageCount(info.pageCount);
      } catch {
        // Page count will remain 0 if read fails
      }
    }
  };

  const handleApply = async () => {
    if (!file || !text.trim()) return;

    const opacityVal = parseFloat(opacity);
    const fontSizeVal = parseInt(fontSize, 10);
    const rotationVal = parseInt(rotation, 10);

    if (isNaN(opacityVal) || opacityVal < 0.05 || opacityVal > 0.5) {
      Alert.alert('Invalid Opacity', 'Opacity must be between 0.05 and 0.5');
      return;
    }
    if (isNaN(fontSizeVal) || fontSizeVal < 12 || fontSizeVal > 96) {
      Alert.alert('Invalid Font Size', 'Font size must be between 12 and 96');
      return;
    }
    if (isNaN(rotationVal) || rotationVal < -90 || rotationVal > 90) {
      Alert.alert('Invalid Rotation', 'Rotation must be between -90 and 90');
      return;
    }

    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setLoading(true);
    try {
      const outPath = await addWatermark(file.uri, text, {
        fontSize: fontSizeVal,
        opacity: opacityVal,
        rotation: rotationVal,
        color: COLOR_PRESETS[colorIdx].value,
      });
      setResultPath(outPath);
      saveRecent(file.name, outPath, pageCount, 'watermark');
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    } catch (e: any) {
      Alert.alert('Watermark Failed', e.message);
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
        <ResultView path={resultPath} onDone={onBack} />
      </View>
    );
  }

  return (
    <View style={s.modalOverlay}>
      <ProcessingOverlay visible={loading} text="Applying watermark…" />
      <View style={s.modalHeader}>
        <Pressable style={s.backBtn} onPress={onBack} accessibilityRole="button" accessibilityLabel="Go back"><Ionicons name="arrow-back" size={24} color={colors.text.primary} /></Pressable>
        <Text style={s.modalTitle}>Watermark PDF</Text>
      </View>
      <ScrollView style={s.modalScroll}>
        {!file ? (
          <Pressable style={s.btnPrimary} onPress={handlePick} accessibilityRole="button" accessibilityLabel="Pick PDF">
            <Text style={s.btnPrimaryText}>Pick PDF</Text>
          </Pressable>
        ) : (
          <>
            <View style={s.fileItem}>
              <Ionicons name="document" size={20} color={colors.type.pdf} style={s.fileIcon} />
              <View style={s.fileInfo}>
                <Text style={s.fileName}>{file.name}</Text>
                {pageCount > 0 && <Text style={s.fileMeta}>{pageCount} pages</Text>}
              </View>
              <Pressable style={s.btnSmall} onPress={() => { setFile(null); setPageCount(0); }} accessibilityRole="button" accessibilityLabel="Clear selected file">
                <Ionicons name="close" size={20} color={colors.text.secondary} />
              </Pressable>
            </View>
            <Text style={[s.label, s.mt16]}>Watermark Text</Text>
            <TextInput
              style={s.input}
              value={text}
              onChangeText={setText}
              placeholder="e.g. CONFIDENTIAL"
              placeholderTextColor={colors.text.tertiary}
            />

            {/* Opacity */}
            <Text style={[s.label, s.mt16]}>Opacity (0.05 – 0.5)</Text>
            <TextInput
              style={s.input}
              value={opacity}
              onChangeText={setOpacity}
              keyboardType="numeric"
              placeholder="0.15"
              placeholderTextColor={colors.text.tertiary}
            />

            {/* Font Size */}
            <Text style={[s.label, s.mt16]}>Font Size (12 – 96)</Text>
            <TextInput
              style={s.input}
              value={fontSize}
              onChangeText={setFontSize}
              keyboardType="numeric"
              placeholder="48"
              placeholderTextColor={colors.text.tertiary}
            />

            {/* Rotation */}
            <Text style={[s.label, s.mt16]}>Rotation (−90 to 90)</Text>
            <TextInput
              style={s.input}
              value={rotation}
              onChangeText={setRotation}
              keyboardType="numeric"
              placeholder="-45"
              placeholderTextColor={colors.text.tertiary}
            />

            {/* Color Presets */}
            <Text style={[s.label, s.mt16]}>Color</Text>
            <View style={s.inputRow}>
              {COLOR_PRESETS.map((preset, i) => (
                <Pressable
                  key={preset.label}
                  onPress={() => { setColorIdx(i); Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); }}
                  accessibilityRole="button"
                  accessibilityLabel={`${preset.label} color`}
                  style={{
                    width: 40, height: 40, borderRadius: 20,
                    backgroundColor: preset.hex,
                    borderWidth: 3,
                    borderColor: i === colorIdx ? colors.accent.primary : 'transparent',
                    alignItems: 'center', justifyContent: 'center',
                  }}
                >
                  {i === colorIdx && <Ionicons name="checkmark" size={18} color="#fff" />}
                </Pressable>
              ))}
            </View>
          </>
        )}
      </ScrollView>
      {file && text.trim() && (
        <View style={s.modalActions}>
          <Pressable style={s.btnPrimary} onPress={handleApply} disabled={loading} accessibilityRole="button" accessibilityLabel="Apply watermark">
            <Text style={s.btnPrimaryText}>Apply Watermark</Text>
          </Pressable>
        </View>
      )}
    </View>
  );
}
