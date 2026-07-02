import React, { useState } from 'react';
import { View, Text, ScrollView, Pressable, Alert, ActivityIndicator } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import Animated, { FadeInDown } from 'react-native-reanimated';
import { colors } from '../../styles/theme';
import s from '../../styles/pdfToolsStyles';
import { getPdfPageInfo } from '../../utils/pdfUtils';
import { reorderPages, rotatePages, addImagePages } from '../../utils/pdfToolsUtils';
import { SelectedFile, PageEntry } from './types';
import ResultView from './ResultView';

interface EditPagesToolProps {
  onBack: () => void;
  onPickFile: () => Promise<SelectedFile[]>;
  onPickImages: () => Promise<SelectedFile[]>;
  saveRecent: (name: string, path: string, pages: number, tool: 'editPages') => void;
}

export default function EditPagesTool({ onBack, onPickFile, onPickImages, saveRecent }: EditPagesToolProps) {
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
        setPages(info.pages.map((p, i) => ({ index: i, width: p.width, height: p.height, rotation: 0 })));
      } catch (e: any) {
        Alert.alert('Error', 'Failed to load PDF pages');
      } finally {
        setLoading(false);
      }
    }
  };

  const movePage = (idx: number, dir: -1 | 1) => {
    const ni = idx + dir;
    if (ni < 0 || ni >= pages.length) return;
    const arr = [...pages];
    [arr[idx], arr[ni]] = [arr[ni], arr[idx]];
    setPages(arr);
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
  };

  const rotatePage = (idx: number) => {
    const arr = [...pages];
    arr[idx] = { ...arr[idx], rotation: (arr[idx].rotation + 90) % 360 };
    setPages(arr);
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
  };

  const deletePage = (idx: number) => {
    if (pages.length <= 1) {
      Alert.alert('Error', 'A PDF must have at least one page.');
      return;
    }
    setPages(prev => prev.filter((_, i) => i !== idx));
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
  };

  const handleAddImages = async () => {
    const imgs = await onPickImages();
    if (!imgs.length || !file) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    setLoading(true);
    try {
      const outPath = await addImagePages(file.uri, pages.length, imgs.map(i => i.uri));
      setFile({ ...file, uri: outPath });
      const info = await getPdfPageInfo(outPath);
      setPages(info.pages.map((p, i) => ({ index: i, width: p.width, height: p.height, rotation: 0 })));
    } catch (e: any) {
      Alert.alert('Error', 'Failed to add images');
    } finally {
      setLoading(false);
    }
  };

  const handleSave = async () => {
    if (!file || pages.length === 0) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setLoading(true);
    try {
      const order = pages.map(p => p.index);
      let outPath = await reorderPages(file.uri, order);
      
      const rotated = pages.filter(p => p.rotation !== 0);
      if (rotated.length > 0) {
        for (const p of rotated) {
          const idx = pages.indexOf(p);
          outPath = await rotatePages(outPath, [idx + 1], p.rotation as 0 | 90 | 180 | 270);
        }
      }
      
      setResultPath(outPath);
      saveRecent(file.name, outPath, pages.length, 'editPages');
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    } catch (e: any) {
      Alert.alert('Save Failed', e.message);
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
        <Text style={s.modalTitle}>Edit Pages</Text>
        {file && !loading && (
          <View style={s.headerRight}>
            <Pressable style={s.btnSmall} onPress={handleAddImages}>
              <Ionicons name="add" size={20} color={colors.accent.primary} />
            </Pressable>
          </View>
        )}
      </View>
      <ScrollView style={s.modalScroll} contentContainerStyle={s.pb100}>
        {!file ? (
          <Pressable style={[s.btnPrimary, s.mb16]} onPress={handlePick}>
            <Text style={s.btnPrimaryText}>Pick PDF</Text>
          </Pressable>
        ) : loading ? (
          <ActivityIndicator size="large" color={colors.accent.primary} style={s.mt20} />
        ) : (
          pages.map((p, i) => (
            <Animated.View key={`page-${i}`} entering={FadeInDown.delay(i * 30)} style={s.pageCard}>
              <View style={s.pageNum}><Text style={s.pageNumText}>{i + 1}</Text></View>
              <View style={s.pageInfo}>
                <Text style={s.pageSize}>{Math.round(p.width)} × {Math.round(p.height)}</Text>
                {p.rotation !== 0 && <Text style={s.pageRotation}>Rotated {p.rotation}°</Text>}
              </View>
              <View style={s.pageActions}>
                <Pressable style={s.btnSmall} onPress={() => movePage(i, -1)} disabled={i === 0}>
                  <Ionicons name="arrow-up" size={14} color={i === 0 ? colors.text.disabled : colors.text.secondary} />
                </Pressable>
                <Pressable style={s.btnSmall} onPress={() => movePage(i, 1)} disabled={i === pages.length - 1}>
                  <Ionicons name="arrow-down" size={14} color={i === pages.length - 1 ? colors.text.disabled : colors.text.secondary} />
                </Pressable>
                <Pressable style={s.btnSmall} onPress={() => rotatePage(i)}>
                  <Ionicons name="refresh" size={14} color={colors.accent.primary} />
                </Pressable>
                <Pressable style={s.btnSmall} onPress={() => deletePage(i)}>
                  <Ionicons name="trash-outline" size={14} color={colors.accent.error} />
                </Pressable>
              </View>
            </Animated.View>
          ))
        )}
      </ScrollView>
      {file && !loading && (
        <View style={s.modalActions}>
          <Pressable style={s.btnPrimary} onPress={handleSave}>
            <Text style={s.btnPrimaryText}>Save Changes</Text>
          </Pressable>
        </View>
      )}
    </View>
  );
}
