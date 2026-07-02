import React, { useState } from 'react';
import { View, Text, ScrollView, Pressable, TextInput, Alert, ActivityIndicator } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import { colors } from '../../styles/theme';
import s from '../../styles/pdfToolsStyles';
import { protectPdf } from '../../utils/pdfToolsUtils';
import { SelectedFile } from './types';
import ResultView from './ResultView';

interface PasswordToolProps {
  onBack: () => void;
  onPickFile: () => Promise<SelectedFile[]>;
}

export default function PasswordTool({ onBack, onPickFile }: PasswordToolProps) {
  const [file, setFile] = useState<SelectedFile | null>(null);
  const [password, setPassword] = useState('');
  const [confirmPw, setConfirmPw] = useState('');
  const [showPw, setShowPw] = useState(false);
  const [loading, setLoading] = useState(false);
  const [resultPath, setResultPath] = useState<string | null>(null);

  const handlePick = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const files = await onPickFile();
    if (files.length) setFile(files[0]);
  };

  const handleProtect = async () => {
    if (!file) return;
    if (!password || password !== confirmPw) {
      Alert.alert('Error', 'Passwords must match');
      return;
    }
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setLoading(true);
    try {
      const outPath = await protectPdf(file.uri, password);
      Alert.alert(
        'Note',
        'PDF-lib does not support native encryption. A copy was saved. For full password protection, use a server-side tool.',
        [{ text: 'OK' }]
      );
      setResultPath(outPath);
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    } catch (e: any) {
      Alert.alert('Protection Failed', e.message);
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
        <Text style={s.modalTitle}>Protect PDF</Text>
      </View>
      <ScrollView style={s.modalScroll}>
        {!file ? (
          <Pressable style={s.btnPrimary} onPress={handlePick}>
            <Text style={s.btnPrimaryText}>Pick PDF</Text>
          </Pressable>
        ) : (
          <>
            <View style={s.fileItem}>
              <Ionicons name="document" size={20} color={colors.type.pdf} style={s.fileIcon} />
              <View style={s.fileInfo}>
                <Text style={s.fileName}>{file.name}</Text>
              </View>
              <Pressable style={s.btnSmall} onPress={() => setFile(null)}>
                <Ionicons name="close" size={20} color={colors.text.secondary} />
              </Pressable>
            </View>
            <Text style={[s.label, s.mt16]}>Set Password</Text>
            <View style={s.inputWrapper}>
              <TextInput
                style={[s.input, { flex: 1 }]}
                value={password}
                onChangeText={setPassword}
                secureTextEntry={!showPw}
                placeholder="Enter password"
                placeholderTextColor={colors.text.tertiary}
              />
              <Pressable style={s.inputAction} onPress={() => setShowPw(!showPw)}>
                <Ionicons name={showPw ? "eye-off" : "eye"} size={20} color={colors.text.secondary} />
              </Pressable>
            </View>
            <Text style={[s.label, s.mt16]}>Confirm Password</Text>
            <TextInput
              style={s.input}
              value={confirmPw}
              onChangeText={setConfirmPw}
              secureTextEntry={!showPw}
              placeholder="Confirm password"
              placeholderTextColor={colors.text.tertiary}
            />
          </>
        )}
      </ScrollView>
      {file && password && (
        <View style={s.modalActions}>
          <Pressable style={s.btnPrimary} onPress={handleProtect} disabled={loading}>
            <Text style={s.btnPrimaryText}>{loading ? 'Protecting...' : 'Apply Protection'}</Text>
          </Pressable>
        </View>
      )}
    </View>
  );
}
