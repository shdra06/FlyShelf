import React, { useState } from 'react';
import { View, Text, ScrollView, Pressable, Alert, ActivityIndicator } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as FileSystem from 'expo-file-system/legacy';
import { colors } from '../../styles/theme';
import s from '../../styles/pdfToolsStyles';
import { getPdfInfo } from '../../utils/pdfToolsUtils';
import { SelectedFile } from './types';

interface InfoToolProps {
  onBack: () => void;
  onPickFile: () => Promise<SelectedFile[]>;
  saveRecent: (name: string, path: string, pages: number, tool: 'info') => void;
}

export default function InfoTool({ onBack, onPickFile, saveRecent }: InfoToolProps) {
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
        <Pressable style={s.backBtn} onPress={onBack}><Ionicons name="arrow-back" size={24} color={colors.text.primary} /></Pressable>
        <Text style={s.modalTitle}>PDF Information</Text>
      </View>
      <ScrollView style={s.modalScroll}>
        {!file ? (
          <Pressable style={s.btnPrimary} onPress={handlePick}>
            <Text style={s.btnPrimaryText}>Pick PDF</Text>
          </Pressable>
        ) : loading ? (
          <ActivityIndicator size="large" color={colors.accent.primary} style={s.mt20} />
        ) : info ? (
          <View style={s.infoCard}>
            <InfoRow label="File Name" value={info.fileName} />
            <InfoRow label="Page Count" value={info.pageCount.toString()} />
            <InfoRow label="File Size" value={formatSize(info.fileSize)} />
            <InfoRow label="Title" value={info.title || 'N/A'} />
            <InfoRow label="Author" value={info.author || 'N/A'} />
            <InfoRow label="Creator" value={info.creator || 'N/A'} />
            <InfoRow label="Producer" value={info.producer || 'N/A'} />
            <InfoRow label="Created" value={info.creationDate ? new Date(info.creationDate).toLocaleString() : 'N/A'} />
            <InfoRow label="Modified" value={info.modificationDate ? new Date(info.modificationDate).toLocaleString() : 'N/A'} />
            <InfoRow label="Encrypted" value={info.isEncrypted ? 'Yes' : 'No'} />
          </View>
        ) : null}
      </ScrollView>
      {file && (
        <View style={s.modalActions}>
          <Pressable style={s.btnSecondary} onPress={() => setFile(null)}>
            <Text style={s.btnSecondaryText}>Pick Another</Text>
          </Pressable>
        </View>
      )}
    </View>
  );
}

const InfoRow = ({ label, value }: { label: string; value: string }) => (
  <View style={s.infoRow}>
    <Text style={s.infoLabel}>{label}</Text>
    <Text style={s.infoValue}>{value}</Text>
  </View>
);
