import { useAppTheme } from '../../hooks/useAppTheme';
import React, { useState, useMemo } from 'react';
import { View, Text, ScrollView, Pressable, Alert, ActivityIndicator } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as FileSystem from 'expo-file-system/legacy';

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
  const [loading, setLoading] = useState(false);

  const handlePick = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const files = await onPickFile();
    if (files.length) {
      setFile(files[0]);
      setLoading(true);
      try {
        const data = await getPdfInfo(files[0].uri);
        const stat = await FileSystem.getInfoAsync(files[0].uri);
        const fullInfo = { ...data, fileSize: (stat as any).size || 0, fileName: files[0].name };
        setInfo(fullInfo);
        saveRecent(files[0].name, files[0].uri, data.pageCount, 'info');
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

  return (
    <View style={s.modalOverlay}>
      <View style={s.modalHeader}>
        <Pressable style={s.backBtn} onPress={onBack} accessibilityRole="button" accessibilityLabel="Go back"><Ionicons name="arrow-back" size={24} color={colors.text.primary} /></Pressable>
        <Text style={s.modalTitle}>PDF Information</Text>
      </View>
      <ScrollView style={s.modalScroll}>
        {!file ? (
          <Pressable style={s.btnPrimary} onPress={handlePick} accessibilityRole="button" accessibilityLabel="Pick PDF file">
            <Text style={s.btnPrimaryText}>Pick PDF</Text>
          </Pressable>
        ) : loading ? (
          <ActivityIndicator size="large" color={colors.accent.primary} style={s.mt20} />
        ) : info ? (
          <View style={s.infoCard}>
            <InfoRow s={s} label="File Name" value={info.fileName} />
            <InfoRow s={s} label="Page Count" value={info.pageCount.toString()} />
            <InfoRow s={s} label="File Size" value={formatSize(info.fileSize)} />
            <InfoRow s={s} label="Title" value={info.title || 'N/A'} />
            <InfoRow s={s} label="Author" value={info.author || 'N/A'} />
            <InfoRow s={s} label="Creator" value={info.creator || 'N/A'} />
            <InfoRow s={s} label="Producer" value={info.producer || 'N/A'} />
            <InfoRow s={s} label="Created" value={info.creationDate ? new Date(info.creationDate).toLocaleString() : 'N/A'} />
            <InfoRow s={s} label="Modified" value={info.modificationDate ? new Date(info.modificationDate).toLocaleString() : 'N/A'} />
            <InfoRow s={s} label="Encrypted" value={info.isEncrypted ? 'Yes' : 'No'} />
          </View>
        ) : null}
      </ScrollView>
      {file && (
        <View style={s.modalActions}>
          <Pressable style={s.btnSecondary} onPress={() => setFile(null)} accessibilityRole="button" accessibilityLabel="Pick another file">
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
