import React, { useState, useMemo, useEffect } from 'react';
import { View, Text, ScrollView, Pressable, Alert, ActivityIndicator, Image } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as FileSystem from 'expo-file-system/legacy';
import * as Clipboard from 'expo-clipboard';
import PdfThumbnail from 'react-native-pdf-thumbnail';
import Animated, { FadeInDown } from 'react-native-reanimated';

import { useAppTheme } from '../../hooks/useAppTheme';
import { createPdfToolsStyles } from '../../styles/pdfToolsStyles';
import { getPdfInfo } from '../../utils/pdfToolsUtils';
import { SelectedFile } from './types';

interface InfoToolProps {
  onBack: () => void;
  onPickFile: () => Promise<SelectedFile[]>;
  saveRecent: (name: string, path: string, pages: number, tool: 'info') => void;
}

export default function InfoTool({ onBack, onPickFile, saveRecent }: InfoToolProps) {
  const { colors, shadows } = useAppTheme();
  const s = useMemo(() => createPdfToolsStyles(colors, shadows), [colors, shadows]);

  const [file, setFile] = useState<SelectedFile | null>(null);
  const [info, setInfo] = useState<any>(null);
  const [thumbUri, setThumbUri] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

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
      setLoading(true);
      try {
        let isEncrypted = false;
        let data: any;
        try {
          data = await getPdfInfo(files[0].uri);
        } catch (e: any) {
          if (e?.message?.toLowerCase().includes('encrypted') || e?.message?.toLowerCase().includes('password')) {
            isEncrypted = true;
            data = {
              pageCount: 0,
              pages: [],
              isEncrypted: true
            };
          } else {
            throw e;
          }
        }
        const stat = await FileSystem.getInfoAsync(files[0].uri);
        
        // Determine if scanned (image-based) or text-based
        // Usually file size per page is a basic heuristic if fonts aren't extracted. 
        // For accurate we'd need font data, but we can do a dummy heuristic.
        const isImageBased = data.pageCount > 0 && ((stat as any).size / data.pageCount > 200000); 

        const fullInfo = { 
          ...data, 
          isEncrypted,
          fileSize: (stat as any).size || 0, 
          fileName: files[0].name,
          fileType: isEncrypted ? 'Unknown (Encrypted)' : (isImageBased ? 'Image-based (Scanned)' : 'Text-based')
        };
        setInfo(fullInfo);
        if (!isEncrypted) {
          saveRecent(files[0].name, files[0].uri, data.pageCount, 'info');
        }
      } catch (e: any) {
        Alert.alert('Error', 'Failed to read PDF info');
      } finally {
        setLoading(false);
      }
    }
  };

  const formatSize = (bytes: number) => {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  };

  const handleCopy = async () => {
    if (!info) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    
    let textToCopy = `File Name: ${info.fileName}\nFile Size: ${formatSize(info.fileSize)}\nPage Count: ${info.pageCount}\nFile Type: ${info.fileType}\nEncrypted: ${info.isEncrypted ? 'Yes' : 'No'}`;
    
    if (!info.isEncrypted) {
      textToCopy += `\nTitle: ${info.title || 'N/A'}\nAuthor: ${info.author || 'N/A'}\nCreator: ${info.creator || 'N/A'}\nProducer: ${info.producer || 'N/A'}`;
      if (info.creationDate) textToCopy += `\nCreated: ${new Date(info.creationDate).toLocaleString()}`;
      if (info.modificationDate) textToCopy += `\nModified: ${new Date(info.modificationDate).toLocaleString()}`;
    }

    await Clipboard.setStringAsync(textToCopy);
    Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    Alert.alert('Copied', 'PDF Information copied to clipboard.');
  };

  const renderPageDimensions = () => {
    if (!info || !info.pages || info.pages.length === 0) return null;
    
    const sizeMap = new Map<string, number[]>();
    info.pages.forEach((p: any, idx: number) => {
      // Basic classification
      let paperSize = 'Custom';
      const w = Math.round(p.width);
      const h = Math.round(p.height);
      if ((w === 595 && h === 842) || (w === 842 && h === 595)) paperSize = 'A4';
      else if ((w === 612 && h === 792) || (w === 792 && h === 612)) paperSize = 'Letter';
      else if ((w === 612 && h === 1008) || (w === 1008 && h === 612)) paperSize = 'Legal';

      const sizeStr = `${w} × ${h} pt (${paperSize})`;
      const pagesArr = sizeMap.get(sizeStr) || [];
      pagesArr.push(idx + 1);
      sizeMap.set(sizeStr, pagesArr);
    });

    return (
      <View style={{ marginTop: 16 }}>
        <Text style={[s.infoLabel, { marginBottom: 8, fontSize: 16, color: colors.text.primary }]}>Page Dimensions</Text>
        {Array.from(sizeMap.entries()).map(([sizeStr, pagesArr]) => {
          const pagesText = pagesArr.length === info.pageCount ? 'All pages' : `Pages: ${pagesArr.length <= 10 ? pagesArr.join(', ') : pagesArr.length + ' pages'}`;
          return (
            <View key={sizeStr} style={{ backgroundColor: colors.bg.input, padding: 8, borderRadius: 8, marginBottom: 8 }}>
              <Text style={{ color: colors.text.primary, fontWeight: '500' }}>{sizeStr}</Text>
              <Text style={{ color: colors.text.tertiary, fontSize: 12 }}>{pagesText}</Text>
            </View>
          );
        })}
      </View>
    );
  };

  return (
    <View style={s.modalOverlay}>
      <View style={s.modalHeader}>
        <Pressable style={s.backBtn} onPress={onBack} accessibilityRole="button" accessibilityLabel="Go back"><Ionicons name="arrow-back" size={24} color={colors.text.primary} /></Pressable>
        <Text style={s.modalTitle}>PDF Information</Text>
        {file && (
          <Pressable onPress={handleCopy} style={{ padding: 8 }} accessibilityRole="button" accessibilityLabel="Copy Info">
            <Ionicons name="copy-outline" size={22} color={colors.accent.primary} />
          </Pressable>
        )}
      </View>
      <ScrollView style={s.modalScroll} contentContainerStyle={{ paddingBottom: 100 }}>
        {!file ? (
          <Pressable style={s.btnPrimary} onPress={handlePick} accessibilityRole="button" accessibilityLabel="Pick PDF file">
            <Text style={s.btnPrimaryText}>Pick PDF</Text>
          </Pressable>
        ) : loading ? (
          <ActivityIndicator size="large" color={colors.accent.primary} style={s.mt20} />
        ) : info ? (
          <Animated.View entering={FadeInDown.duration(300)}>
            {/* Thumbnail */}
            <View style={{ width: '100%', alignItems: 'center', marginBottom: 20 }}>
              <View style={{ width: 160, height: 220, backgroundColor: colors.bg.elevated, borderRadius: 12, overflow: 'hidden', alignItems: 'center', justifyContent: 'center', ...shadows.medium, borderWidth: 1, borderColor: colors.border.subtle }}>
                {thumbUri ? (
                  <Image source={{ uri: thumbUri }} style={{ width: '100%', height: '100%', resizeMode: 'cover' }} />
                ) : (
                  <Ionicons name="document-text" size={64} color={colors.text.tertiary} />
                )}
              </View>
            </View>

            <View style={s.infoCard}>
              <InfoRow s={s} label="File Name" value={info.fileName} />
              <InfoRow s={s} label="Page Count" value={info.pageCount.toString()} />
              <InfoRow s={s} label="File Size" value={formatSize(info.fileSize)} />
              <InfoRow s={s} label="File Type" value={info.fileType} />
              <InfoRow s={s} label="Encrypted" value={info.isEncrypted ? 'Yes' : 'No'} />
              
              {!info.isEncrypted && (
                <>
                  <InfoRow s={s} label="Title" value={info.title || 'N/A'} />
                  <InfoRow s={s} label="Author" value={info.author || 'N/A'} />
                  <InfoRow s={s} label="Creator" value={info.creator || 'N/A'} />
                  <InfoRow s={s} label="Producer" value={info.producer || 'N/A'} />
                  <InfoRow s={s} label="Created" value={info.creationDate ? new Date(info.creationDate).toLocaleString() : 'N/A'} />
                  <InfoRow s={s} label="Modified" value={info.modificationDate ? new Date(info.modificationDate).toLocaleString() : 'N/A'} />
                </>
              )}
            </View>

            {!info.isEncrypted && renderPageDimensions()}
          </Animated.View>
        ) : null}
      </ScrollView>
      {file && (
        <View style={s.modalActions}>
          <Pressable style={s.btnSecondary} onPress={() => { setFile(null); setThumbUri(null); }} accessibilityRole="button" accessibilityLabel="Pick another file">
            <Text style={s.btnSecondaryText}>Pick Another</Text>
          </Pressable>
        </View>
      )}
    </View>
  );
}

const InfoRow = ({ label, value, s }: { label: string; value: string; s: any }) => (
  <View style={s.infoRow}>
    <Text style={s.infoLabel}>{label}</Text>
    <Text style={s.infoValue}>{value}</Text>
  </View>
);
