import React, { useState, useMemo, useEffect } from 'react';
import { View, Text, ScrollView, Pressable, TextInput, Alert, Image } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import PdfThumbnail from 'react-native-pdf-thumbnail';
import Animated, { FadeInDown } from 'react-native-reanimated';

import { useAppTheme } from '../../hooks/useAppTheme';
import { createPdfToolsStyles } from '../../styles/pdfToolsStyles';
import { addWatermark } from '../../utils/pdfToolsUtils';
import { getPdfPageInfo } from '../../utils/pdfUtils';
import { SelectedFile } from './types';
import ResultView from './ResultView';
import ProcessingOverlay from './ProcessingOverlay';

interface WatermarkToolProps {
  onBack: () => void;
  onPickFile: () => Promise<SelectedFile[]>;
  saveRecent: (name: string, path: string, pages: number, tool: 'watermark') => void;
  onSendToPc?: (filePath: string) => void;
}

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

type PositionPreset = 'Diagonal' | 'Center' | 'Top' | 'Bottom' | 'Top-Left' | 'Bottom-Right';
const POSITIONS: PositionPreset[] = ['Diagonal', 'Center', 'Top', 'Bottom', 'Top-Left', 'Bottom-Right'];
type PageRangeType = 'All Pages' | 'Even Pages' | 'Odd Pages' | 'Custom';

function parseCustomRange(rangeStr: string, totalPages: number): number[] {
  const pages = new Set<number>();
  const parts = rangeStr.split(',');
  for (const part of parts) {
    const trimmed = part.trim();
    if (!trimmed) continue;
    if (trimmed.includes('-')) {
      const [startStr, endStr] = trimmed.split('-');
      const s = parseInt(startStr, 10);
      const e = parseInt(endStr, 10);
      if (!isNaN(s) && !isNaN(e) && s <= e) {
        for (let i = s; i <= e; i++) {
          if (i >= 1 && i <= totalPages) pages.add(i - 1);
        }
      }
    } else {
      const p = parseInt(trimmed, 10);
      if (!isNaN(p) && p >= 1 && p <= totalPages) {
        pages.add(p - 1);
      }
    }
  }
  return Array.from(pages);
}

export default function WatermarkTool({ onBack, onPickFile, saveRecent, onSendToPc }: WatermarkToolProps) {
  const { colors, shadows } = useAppTheme();
  const s = useMemo(() => createPdfToolsStyles(colors, shadows), [colors, shadows]);

  const [file, setFile] = useState<SelectedFile | null>(null);
  const [pageCount, setPageCount] = useState(0);
  const [text, setText] = useState('CONFIDENTIAL');
  const [opacity, setOpacity] = useState('0.15');
  const [fontSize, setFontSize] = useState('48');
  const [rotation, setRotation] = useState('-45');
  const [colorIdx, setColorIdx] = useState(0);
  const [position, setPosition] = useState<PositionPreset>('Diagonal');
  const [isBold, setIsBold] = useState(false);
  const [pageRange, setPageRange] = useState<PageRangeType>('All Pages');
  const [customRange, setCustomRange] = useState('');
  
  const [thumbUri, setThumbUri] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [resultPath, setResultPath] = useState<string | null>(null);

  useEffect(() => {
    if (file) {
      const uri = file.uri.startsWith('file://') ? file.uri : (file.uri.startsWith('/') ? `file://${file.uri}` : file.uri);
      PdfThumbnail.generate(uri, 0, 80)
        .then(result => setThumbUri(result.uri))
        .catch(() => setThumbUri(null));
    } else {
      setThumbUri(null);
    }
  }, [file]);

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

    let targetPages: number[] = [];
    if (pageRange === 'All Pages') {
      targetPages = Array.from({ length: pageCount }, (_, i) => i);
    } else if (pageRange === 'Even Pages') {
      targetPages = Array.from({ length: pageCount }, (_, i) => i).filter(i => (i + 1) % 2 === 0);
    } else if (pageRange === 'Odd Pages') {
      targetPages = Array.from({ length: pageCount }, (_, i) => i).filter(i => (i + 1) % 2 !== 0);
    } else {
      targetPages = parseCustomRange(customRange, pageCount);
      if (targetPages.length === 0) {
        Alert.alert('Invalid Range', 'Please enter a valid page range.');
        return;
      }
    }

    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setLoading(true);
    try {
      const outPath = await addWatermark(file.uri, text, {
        fontSize: fontSizeVal,
        opacity: opacityVal,
        rotation: rotationVal,
        color: COLOR_PRESETS[colorIdx].value,
        pages: targetPages,
        position,
        isBold
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

  const renderPositionButton = (pos: PositionPreset) => {
    const isSelected = position === pos;
    return (
      <Pressable 
        key={pos} 
        style={[s.btnSmall, { flex: 1, margin: 4, borderWidth: isSelected ? 2 : 1, borderColor: isSelected ? colors.accent.primary : colors.border.medium, backgroundColor: isSelected ? colors.accent.primaryDim : colors.bg.elevated }]}
        onPress={() => { setPosition(pos); Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); }}
      >
        <Text style={{ color: isSelected ? colors.accent.primary : colors.text.secondary, fontSize: 12, fontWeight: isSelected ? '600' : '400' }}>{pos}</Text>
      </Pressable>
    );
  };

  const getPreviewStyles = () => {
    const opacityVal = parseFloat(opacity);
    const isValidOpacity = !isNaN(opacityVal) && opacityVal >= 0.05 && opacityVal <= 1;
    let computedRotation = rotation;
    if (position === 'Center' || position === 'Top' || position === 'Bottom' || position === 'Top-Left' || position === 'Bottom-Right') {
      computedRotation = '0';
    }
    
    let align: any = 'center';
    let justify: any = 'center';
    
    switch (position) {
      case 'Top': justify = 'flex-start'; break;
      case 'Bottom': justify = 'flex-end'; break;
      case 'Top-Left': justify = 'flex-start'; align = 'flex-start'; break;
      case 'Bottom-Right': justify = 'flex-end'; align = 'flex-end'; break;
      case 'Center':
      case 'Diagonal':
      default:
        break;
    }

    return {
      opacity: isValidOpacity ? opacityVal : 0.15,
      transform: [{ rotate: `${computedRotation}deg` }],
      color: COLOR_PRESETS[colorIdx].hex,
      fontSize: 24, // scaled down for preview
      fontWeight: isBold ? 'bold' as const : 'normal' as const,
      alignItems: align,
      justifyContent: justify,
    };
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

  const pStyles = getPreviewStyles();

  return (
    <View style={s.modalOverlay}>
      <ProcessingOverlay visible={loading} text="Applying watermark…" />
      <View style={s.modalHeader}>
        <Pressable style={s.backBtn} onPress={onBack} accessibilityRole="button" accessibilityLabel="Go back"><Ionicons name="arrow-back" size={24} color={colors.text.primary} /></Pressable>
        <Text style={s.modalTitle}>Watermark PDF</Text>
      </View>
      <ScrollView style={s.modalScroll} contentContainerStyle={{ paddingBottom: 100 }}>
        {!file ? (
          <Pressable style={s.btnPrimary} onPress={handlePick} accessibilityRole="button" accessibilityLabel="Pick PDF">
            <Text style={s.btnPrimaryText}>Pick PDF</Text>
          </Pressable>
        ) : (
          <Animated.View entering={FadeInDown.duration(300)}>
            {/* Live Preview Card */}
            {thumbUri && (
              <View style={{ width: '100%', height: 200, backgroundColor: colors.bg.elevated, borderRadius: 12, overflow: 'hidden', alignItems: 'center', justifyContent: 'center', marginBottom: 16 }}>
                <Image source={{ uri: thumbUri }} style={{ width: 140, height: 198, resizeMode: 'contain' }} />
                <View style={[{ position: 'absolute', width: 140, height: 198, padding: 8, alignItems: pStyles.alignItems, justifyContent: pStyles.justifyContent }]}>
                  <Text style={{ color: pStyles.color, opacity: pStyles.opacity, transform: pStyles.transform, fontWeight: pStyles.fontWeight, fontSize: pStyles.fontSize, textAlign: 'center' }}>
                    {text || 'Text'}
                  </Text>
                </View>
              </View>
            )}

            <View style={s.fileItem}>
              <Ionicons name="document" size={20} color={colors.type.pdf} style={s.fileIcon} />
              <View style={s.fileInfo}>
                <Text style={s.fileName}>{file.name}</Text>
                {pageCount > 0 && <Text style={s.fileMeta}>{pageCount} pages</Text>}
              </View>
              <Pressable style={s.btnSmall} onPress={() => { setFile(null); setPageCount(0); setThumbUri(null); }} accessibilityRole="button" accessibilityLabel="Clear selected file">
                <Ionicons name="close" size={20} color={colors.text.secondary} />
              </Pressable>
            </View>

            {/* Page Range */}
            <Text style={[s.label, s.mt16]}>Page Range</Text>
            <View style={{ flexDirection: 'row', flexWrap: 'wrap', marginTop: 8 }}>
              {(['All Pages', 'Even Pages', 'Odd Pages', 'Custom'] as PageRangeType[]).map(rt => (
                <Pressable 
                  key={rt} 
                  style={{ flexDirection: 'row', alignItems: 'center', marginRight: 16, marginBottom: 8 }}
                  onPress={() => { setPageRange(rt); Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); }}
                >
                  <Ionicons name={pageRange === rt ? 'radio-button-on' : 'radio-button-off'} size={20} color={pageRange === rt ? colors.accent.primary : colors.text.tertiary} />
                  <Text style={{ color: colors.text.primary, marginLeft: 6 }}>{rt}</Text>
                </Pressable>
              ))}
            </View>
            {pageRange === 'Custom' && (
              <TextInput
                style={[s.input, { marginTop: 8 }]}
                value={customRange}
                onChangeText={setCustomRange}
                placeholder="e.g. 1-3, 5, 8-10"
                placeholderTextColor={colors.text.tertiary}
              />
            )}

            {/* Position Presets */}
            <Text style={[s.label, s.mt16]}>Position</Text>
            <View style={{ flexDirection: 'row', marginTop: 8 }}>
              {renderPositionButton('Top-Left')}
              {renderPositionButton('Top')}
              {renderPositionButton('Center')}
            </View>
            <View style={{ flexDirection: 'row', marginTop: 8 }}>
              {renderPositionButton('Bottom-Right')}
              {renderPositionButton('Bottom')}
              {renderPositionButton('Diagonal')}
            </View>

            <Text style={[s.label, s.mt16]}>Watermark Text</Text>
            <TextInput
              style={s.input}
              value={text}
              onChangeText={setText}
              placeholder="e.g. CONFIDENTIAL"
              placeholderTextColor={colors.text.tertiary}
            />

            <View style={{ flexDirection: 'row', alignItems: 'center', marginTop: 16 }}>
              <Pressable 
                style={{ flexDirection: 'row', alignItems: 'center' }}
                onPress={() => { setIsBold(!isBold); Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); }}
              >
                <Ionicons name={isBold ? 'checkbox' : 'square-outline'} size={20} color={isBold ? colors.accent.primary : colors.text.tertiary} />
                <Text style={{ color: colors.text.primary, marginLeft: 8, fontWeight: '500' }}>Bold Text</Text>
              </Pressable>
            </View>

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
            {position === 'Diagonal' && (
              <>
                <Text style={[s.label, s.mt16]}>Rotation (−90 to 90)</Text>
                <TextInput
                  style={s.input}
                  value={rotation}
                  onChangeText={setRotation}
                  keyboardType="numeric"
                  placeholder="-45"
                  placeholderTextColor={colors.text.tertiary}
                />
              </>
            )}

            {/* Color Presets */}
            <Text style={[s.label, s.mt16]}>Color</Text>
            <View style={[s.inputRow, { marginTop: 8 }]}>
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
                    marginRight: 12
                  }}
                >
                  {i === colorIdx && <Ionicons name="checkmark" size={18} color="#fff" />}
                </Pressable>
              ))}
            </View>
          </Animated.View>
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
