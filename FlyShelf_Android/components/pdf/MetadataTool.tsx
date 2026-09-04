import React, { useState, useMemo } from 'react';
import { View, Text, ScrollView, Pressable, TextInput, Alert, ActivityIndicator } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as Clipboard from 'expo-clipboard';
import Animated, { FadeInDown } from 'react-native-reanimated';

import { useAppTheme } from '../../hooks/useAppTheme';
import { createPdfToolsStyles } from '../../styles/pdfToolsStyles';
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
  const { colors, shadows } = useAppTheme();
  const s = useMemo(() => createPdfToolsStyles(colors, shadows), [colors, shadows]);

  const [file, setFile] = useState<SelectedFile | null>(null);
  const [pageCount, setPageCount] = useState(0);
  const [title, setTitle] = useState('');
  const [author, setAuthor] = useState('');
  const [subject, setSubject] = useState('');
  const [keywords, setKeywords] = useState('');
  
  // Read-only fields
  const [creator, setCreator] = useState('');
  const [producer, setProducer] = useState('');
  const [creationDate, setCreationDate] = useState('');
  const [modDate, setModDate] = useState('');

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
        // pdf-lib's getKeywords is not directly exposed in our getPdfInfo so we would need it there, but we can just use empty if not available, wait, we didn't add getKeywords to getPdfInfo, let's leave keywords empty initially if it wasn't there
        setKeywords((info as any).keywords ? (info as any).keywords.join(', ') : '');
        
        setCreator(info.creator || '');
        setProducer(info.producer || '');
        setCreationDate(info.creationDate ? new Date(info.creationDate).toLocaleString() : '');
        setModDate(info.modificationDate ? new Date(info.modificationDate).toLocaleString() : '');
      } catch (err: any) {
        Alert.alert('Read Error', err?.message || 'Could not read PDF metadata. You can still set new values.');
      }
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

  const handleClearAll = () => {
    Alert.alert('Clear Metadata', 'Are you sure you want to strip all metadata from this PDF? This action cannot be undone once saved.', [
      { text: 'Cancel', style: 'cancel' },
      { 
        text: 'Clear All', 
        style: 'destructive',
        onPress: () => {
          Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Heavy);
          setTitle('');
          setAuthor('');
          setSubject('');
          setKeywords('');
        }
      }
    ]);
  };

  const handleCopyAll = async () => {
    if (!file) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const metaText = `Title: ${title || 'N/A'}\nAuthor: ${author || 'N/A'}\nSubject: ${subject || 'N/A'}\nKeywords: ${keywords || 'N/A'}\nCreator: ${creator || 'N/A'}\nProducer: ${producer || 'N/A'}\nCreated: ${creationDate || 'N/A'}\nModified: ${modDate || 'N/A'}`;
    await Clipboard.setStringAsync(metaText);
    Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    Alert.alert('Copied', 'Metadata copied to clipboard.');
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
        {file && (
          <Pressable onPress={handleCopyAll} style={{ padding: 8 }} accessibilityRole="button" accessibilityLabel="Copy All">
            <Ionicons name="copy-outline" size={22} color={colors.accent.primary} />
          </Pressable>
        )}
      </View>
      <ScrollView style={s.modalScroll} contentContainerStyle={{ paddingBottom: 100 }}>
        {!file ? (
          <Pressable style={s.btnPrimary} onPress={handlePick} accessibilityRole="button" accessibilityLabel="Pick PDF file">
            <Text style={s.btnPrimaryText}>Pick PDF</Text>
          </Pressable>
        ) : (
          <Animated.View entering={FadeInDown.duration(300)}>
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

            {/* Read-Only Info Card */}
            <View style={{ backgroundColor: colors.bg.elevated, padding: 16, borderRadius: 12, marginTop: 16, borderWidth: 1, borderColor: colors.border.subtle }}>
              <View style={{ flexDirection: 'row', alignItems: 'center', marginBottom: 12 }}>
                <Ionicons name="information-circle-outline" size={20} color={colors.text.tertiary} />
                <Text style={{ color: colors.text.secondary, fontWeight: '600', marginLeft: 8 }}>Read-Only Info</Text>
              </View>
              <View style={{ flexDirection: 'row', justifyContent: 'space-between', marginBottom: 4 }}>
                <Text style={{ color: colors.text.tertiary, fontSize: 13 }}>Creator:</Text>
                <Text style={{ color: colors.text.secondary, fontSize: 13 }}>{creator || 'Unknown'}</Text>
              </View>
              <View style={{ flexDirection: 'row', justifyContent: 'space-between', marginBottom: 4 }}>
                <Text style={{ color: colors.text.tertiary, fontSize: 13 }}>Producer:</Text>
                <Text style={{ color: colors.text.secondary, fontSize: 13 }}>{producer || 'Unknown'}</Text>
              </View>
              <View style={{ flexDirection: 'row', justifyContent: 'space-between', marginBottom: 4 }}>
                <Text style={{ color: colors.text.tertiary, fontSize: 13 }}>Created:</Text>
                <Text style={{ color: colors.text.secondary, fontSize: 13 }}>{creationDate || 'Unknown'}</Text>
              </View>
              <View style={{ flexDirection: 'row', justifyContent: 'space-between' }}>
                <Text style={{ color: colors.text.tertiary, fontSize: 13 }}>Modified:</Text>
                <Text style={{ color: colors.text.secondary, fontSize: 13 }}>{modDate || 'Unknown'}</Text>
              </View>
            </View>

            <Text style={[s.label, s.mt16]}>Title</Text>
            <Text style={{ color: colors.text.tertiary, fontSize: 12, marginBottom: 4 }}>The document's title</Text>
            <TextInput style={s.input} value={title} onChangeText={setTitle} placeholder="Document Title" placeholderTextColor={colors.text.tertiary} />
            
            <Text style={[s.label, s.mt16]}>Author</Text>
            <Text style={{ color: colors.text.tertiary, fontSize: 12, marginBottom: 4 }}>The person who wrote the document</Text>
            <TextInput style={s.input} value={author} onChangeText={setAuthor} placeholder="Author Name" placeholderTextColor={colors.text.tertiary} />
            
            <Text style={[s.label, s.mt16]}>Subject</Text>
            <Text style={{ color: colors.text.tertiary, fontSize: 12, marginBottom: 4 }}>The topic of the document</Text>
            <TextInput style={s.input} value={subject} onChangeText={setSubject} placeholder="Brief Subject" placeholderTextColor={colors.text.tertiary} />
            
            <Text style={[s.label, s.mt16]}>Keywords</Text>
            <Text style={{ color: colors.text.tertiary, fontSize: 12, marginBottom: 4 }}>Comma-separated tags for search</Text>
            <TextInput style={s.input} value={keywords} onChangeText={setKeywords} placeholder="e.g. report, annual, 2023" placeholderTextColor={colors.text.tertiary} />
            
            <Pressable 
              style={{ marginTop: 24, alignSelf: 'center', flexDirection: 'row', alignItems: 'center' }} 
              onPress={handleClearAll}
            >
              <Ionicons name="trash-outline" size={20} color={colors.accent.error} />
              <Text style={{ color: colors.accent.error, marginLeft: 8, fontWeight: '600' }}>Clear All Metadata</Text>
            </Pressable>
          </Animated.View>
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
