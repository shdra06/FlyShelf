import React, { useState } from 'react';
import { View, Text, ScrollView, Pressable, TextInput, Alert, ActivityIndicator } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import { colors } from '../../styles/theme';
import s from '../../styles/pdfToolsStyles';
import { getPdfInfo, setPdfMetadata } from '../../utils/pdfToolsUtils';
import { getPdfPageInfo } from '../../utils/pdfUtils';
import { SelectedFile } from './types';
import ResultView from './ResultView';
import ProcessingOverlay from './ProcessingOverlay';

interface MetadataToolProps {
  onBack: () => void;
  onPickFile: () => Promise<SelectedFile[]>;
  saveRecent?: (name: string, path: string, pages: number, tool: 'metadata') => void;
}

export default function MetadataTool({ onBack, onPickFile, saveRecent }: MetadataToolProps) {
  const [file, setFile] = useState<SelectedFile | null>(null);
  const [pageCount, setPageCount] = useState(0);
  const [title, setTitle] = useState('');
  const [author, setAuthor] = useState('');
  const [subject, setSubject] = useState('');
  const [keywords, setKeywords] = useState('');
  const [loading, setLoading] = useState(false);
  const [resultPath, setResultPath] = useState<string | null>(null);

  const handlePick = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const files = await onPickFile();
    if (files.length) {
      setFile(files[0]);
      setLoading(true);
      try {
        const info = await getPdfInfo(files[0].uri);
        setTitle(info.title || '');
        setAuthor(info.author || '');
        setSubject(info.subject || '');
        setKeywords('');
      } catch (err: any) {
        Alert.alert('Read Error', err?.message || 'Could not read PDF metadata. You can still set new values.');
      }
      // Read page count separately so it succeeds even if metadata read fails
      try {
        const pageInfo = await getPdfPageInfo(files[0].uri);
        setPageCount(pageInfo.pageCount);
      } catch {
        // Page count will remain 0 if read fails
      }
      setLoading(false);
    }
  };

  const handleSave = async () => {
    if (!file) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setLoading(true);
    try {
      const outPath = await setPdfMetadata(file.uri, {
        title: title || undefined,
        author: author || undefined,
        subject: subject || undefined,
        keywords: keywords ? keywords.split(',').map(k => k.trim()) : undefined,
      });
      setResultPath(outPath);
      saveRecent?.(file.name, outPath, pageCount, 'metadata');
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
          <Pressable style={s.backBtn} onPress={onBack} accessibilityRole="button" accessibilityLabel="Go back"><Ionicons name="arrow-back" size={24} color={colors.text.primary} /></Pressable>
          <Text style={s.modalTitle}>Success</Text>
        </View>
        <ResultView path={resultPath} onDone={onBack} />
      </View>
    );
  }

  return (
    <View style={s.modalOverlay}>
      <ProcessingOverlay visible={loading && !!file} text="Saving metadata…" />
      <View style={s.modalHeader}>
        <Pressable style={s.backBtn} onPress={onBack} accessibilityRole="button" accessibilityLabel="Go back"><Ionicons name="arrow-back" size={24} color={colors.text.primary} /></Pressable>
        <Text style={s.modalTitle}>Edit Metadata</Text>
      </View>
      <ScrollView style={s.modalScroll}>
        {!file ? (
          <Pressable style={s.btnPrimary} onPress={handlePick} accessibilityRole="button" accessibilityLabel="Pick PDF file">
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
              <Pressable style={s.btnSmall} onPress={() => { setFile(null); setPageCount(0); }} accessibilityRole="button" accessibilityLabel="Remove selected file">
                <Ionicons name="close" size={20} color={colors.text.secondary} />
              </Pressable>
            </View>
            <Text style={[s.label, s.mt16]}>Title</Text>
            <TextInput style={s.input} value={title} onChangeText={setTitle} placeholder="Document Title" placeholderTextColor={colors.text.tertiary} />
            
            <Text style={[s.label, s.mt16]}>Author</Text>
            <TextInput style={s.input} value={author} onChangeText={setAuthor} placeholder="Author Name" placeholderTextColor={colors.text.tertiary} />
            
            <Text style={[s.label, s.mt16]}>Subject</Text>
            <TextInput style={s.input} value={subject} onChangeText={setSubject} placeholder="Brief Subject" placeholderTextColor={colors.text.tertiary} />
            
            <Text style={[s.label, s.mt16]}>Keywords (comma separated)</Text>
            <TextInput style={s.input} value={keywords} onChangeText={setKeywords} placeholder="key1, key2" placeholderTextColor={colors.text.tertiary} />
          </>
        )}
      </ScrollView>
      {file && (
        <View style={s.modalActions}>
          <Pressable style={s.btnPrimary} onPress={handleSave} disabled={loading} accessibilityRole="button" accessibilityLabel="Save metadata">
            <Text style={s.btnPrimaryText}>{loading ? 'Saving...' : 'Save Metadata'}</Text>
          </Pressable>
        </View>
      )}
    </View>
  );
}
