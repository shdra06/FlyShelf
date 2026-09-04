import { useAppTheme } from '../../hooks/useAppTheme';
import React, { useState, useEffect, useMemo } from 'react';
import { View, Text, ScrollView, Pressable, TextInput, Alert, ActivityIndicator } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import AsyncStorage from '@react-native-async-storage/async-storage';
import * as FileSystem from 'expo-file-system/legacy';

import { createPdfToolsStyles } from '../../styles/pdfToolsStyles';
import { SelectedFile } from './types';
import ResultView from './ResultView';
import { useSettings } from '../../context/SettingsContext';
import { resolveBestPcUrl } from '../../utils/networkHelpers';
import { PairedDevice } from '../../utils/deviceTypes';
import ProcessingOverlay from './ProcessingOverlay';
import { space, radius } from '../../styles/theme';

interface PasswordToolProps {
  onBack: () => void;
  onPickFile: () => Promise<SelectedFile[]>;
}

export default function PasswordTool({ onBack, onPickFile }: PasswordToolProps) {
  const { colors, shadows } = useAppTheme();
  const s = useMemo(() => createPdfToolsStyles(colors, shadows), [colors, shadows]);
  const { settings } = useSettings();

  const [file, setFile] = useState<SelectedFile | null>(null);
  const [password, setPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [showPw, setShowPw] = useState(false);
  const [hasPc, setHasPc] = useState(false);
  const [loading, setLoading] = useState(false);
  const [resultPath, setResultPath] = useState<string | null>(null);

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
    if (files.length) setFile(files[0]);
  };

  const getPasswordStrength = () => {
    if (!password) return null;
    if (password.length < 6) return { label: 'Weak', color: colors.accent.error };
    const hasNum = /\d/.test(password);
    const hasSpecial = /[!@#$%^&*(),.?":{}|<>]/.test(password);
    if (password.length >= 8 && hasNum && hasSpecial) return { label: 'Strong', color: colors.accent.success };
    return { label: 'Medium', color: colors.accent.warning };
  };

  const strength = getPasswordStrength();

  const handleProtect = async () => {
    if (password !== confirmPassword) {
      Alert.alert('Error', 'Passwords do not match');
      return;
    }
    if (!hasPc || !file) return;

    setLoading(true);
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    try {
      const data = await AsyncStorage.getItem('@flyshelf_paired_devices');
      const devices: PairedDevice[] = data ? JSON.parse(data) : [];
      const pc = devices.find(d => d.deviceType === 'PC');
      if (!pc) throw new Error('No paired PC found.');

      const pcUrl = await resolveBestPcUrl(pc);
      if (!pcUrl) throw new Error('Could not connect to paired PC.');

      const b64 = await FileSystem.readAsStringAsync(file.uri, { encoding: FileSystem.EncodingType.Base64 });

      const response = await fetch(`${pcUrl}/api/protect_pdf`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-PDF-Password': password,
        },
        body: JSON.stringify({
          filename: file.name,
          data: b64,
        }),
      });

      if (!response.ok) throw new Error('PC failed to encrypt the PDF.');
      const resData = await response.json();
      if (!resData.data) throw new Error('Invalid response from PC.');

      const OUTPUT_DIR = `${FileSystem.documentDirectory}FlyShelf/PDFTools/`;
      await FileSystem.makeDirectoryAsync(OUTPUT_DIR, { intermediates: true }).catch(() => {});
      const outPath = `${OUTPUT_DIR}encrypted_${Date.now()}.pdf`;
      
      await FileSystem.writeAsStringAsync(outPath, resData.data, { encoding: FileSystem.EncodingType.Base64 });
      setResultPath(outPath);
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    } catch (e: any) {
      Alert.alert('Encryption Failed', e.message);
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
          <Text style={s.modalTitle}>Success</Text>
        </View>
        <ResultView path={resultPath} onDone={onBack} />
      </View>
    );
  }

  return (
    <View style={s.modalOverlay}>
      <ProcessingOverlay visible={loading} text="Encrypting PDF via PC…" />
      <View style={s.modalHeader}>
        <Pressable style={s.backBtn} onPress={onBack} accessibilityRole="button" accessibilityLabel="Go back"><Ionicons name="arrow-back" size={24} color={colors.text.primary} /></Pressable>
        <Text style={s.modalTitle}>Protect PDF</Text>
      </View>
      <ScrollView style={s.modalScroll} contentContainerStyle={{ paddingBottom: 40 }}>
        {!hasPc ? (
          <View style={{ backgroundColor: colors.accent.warningDim, borderRadius: radius.md, padding: space.md, marginBottom: space.lg, borderWidth: 1, borderColor: colors.accent.warning }}>
            <Text style={{ color: colors.accent.warning, fontSize: 14, fontWeight: '600' }}>⚠️ Desktop Required</Text>
            <Text style={{ color: colors.text.secondary, fontSize: 13, marginTop: 4 }}>
              PDF encryption requires a paired desktop computer. Pair your PC in Settings to unlock this feature.
            </Text>
          </View>
        ) : null}

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
              </View>
              <Pressable style={s.btnSmall} onPress={() => setFile(null)} accessibilityRole="button" accessibilityLabel="Remove selected file">
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
              <Pressable style={s.inputAction} onPress={() => setShowPw(!showPw)} accessibilityRole="button" accessibilityLabel={showPw ? 'Hide password' : 'Show password'}>
                <Ionicons name={showPw ? "eye-off" : "eye"} size={20} color={colors.text.secondary} />
              </Pressable>
            </View>
            
            {strength && (
              <View style={{ flexDirection: 'row', alignItems: 'center', marginTop: 8, paddingHorizontal: 4 }}>
                <View style={{ flex: 1, height: 4, backgroundColor: colors.border.subtle, borderRadius: 2, overflow: 'hidden' }}>
                  <View style={{ width: strength.label === 'Weak' ? '33%' : strength.label === 'Medium' ? '66%' : '100%', height: '100%', backgroundColor: strength.color }} />
                </View>
                <Text style={{ color: strength.color, fontSize: 12, fontWeight: '600', marginLeft: 8 }}>{strength.label}</Text>
              </View>
            )}

            <Text style={[s.label, s.mt16]}>Confirm Password</Text>
            <View style={s.inputWrapper}>
              <TextInput
                style={[s.input, { flex: 1 }]}
                value={confirmPassword}
                onChangeText={setConfirmPassword}
                secureTextEntry={!showPw}
                placeholder="Confirm password"
                placeholderTextColor={colors.text.tertiary}
              />
            </View>

            {hasPc && password && confirmPassword && (
              <View style={[s.modalActions, { marginTop: space.xl, padding: 0 }]}>
                <Pressable 
                  style={[s.btnPrimary, { opacity: (password && password === confirmPassword) ? 1 : 0.5 }]} 
                  onPress={handleProtect} 
                  disabled={password !== confirmPassword} 
                  accessibilityRole="button"
                >
                  <Text style={s.btnPrimaryText}>Encrypt PDF</Text>
                </Pressable>
              </View>
            )}
            {!hasPc && password && (
              <View style={[s.modalActions, { marginTop: space.xl, padding: 0 }]}>
                <Pressable style={[s.btnPrimary, { opacity: 0.4 }]} disabled accessibilityRole="button">
                  <Text style={s.btnPrimaryText}>Protection Unavailable</Text>
                </Pressable>
              </View>
            )}
          </>
        )}
      </ScrollView>
    </View>
  );
}
