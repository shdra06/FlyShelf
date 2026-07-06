import React, { useState } from 'react';
import { View, Text, ScrollView, Pressable, TextInput, Alert } from 'react-native';
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
  const [showPw, setShowPw] = useState(false);

  const handlePick = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const files = await onPickFile();
    if (files.length) setFile(files[0]);
  };

  // NOTE: protectPdf() in pdfToolsUtils.ts always throws when a password is
  // provided because pdf-lib does not support native PDF encryption. A proper
  // native module (e.g. react-native-pdf-lib with encryption, or a server-side
  // API) is required before this feature can work. Until then the button is
  // disabled and the handler shows an informational alert.
  const handleProtect = async () => {
    Alert.alert(
      'Feature Unavailable',
      'PDF password protection requires a native encryption module that is not yet installed. '
      + 'pdf-lib (the current library) does not support PDF encryption. '
      + 'Please use a server-side tool or install a native module to enable this feature.',
      [{ text: 'OK' }]
    );
  };



  return (
    <View style={s.modalOverlay}>
      <View style={s.modalHeader}>
        <Pressable style={s.backBtn} onPress={onBack} accessibilityRole="button" accessibilityLabel="Go back"><Ionicons name="arrow-back" size={24} color={colors.text.primary} /></Pressable>
        <Text style={s.modalTitle}>Protect PDF</Text>
      </View>
      <ScrollView style={s.modalScroll}>
        {/* Warning: encryption is not supported by pdf-lib */}
        <View style={{ backgroundColor: '#3B2A1A', borderRadius: 10, padding: 12, marginBottom: 12, borderWidth: 1, borderColor: '#7C5C28' }}>
          <Text style={{ color: '#FFD580', fontSize: 13, fontWeight: '600' }}>⚠️ Feature Unavailable</Text>
          <Text style={{ color: '#CCAA66', fontSize: 12, marginTop: 4 }}>
            PDF password protection requires a native encryption module not yet installed. This tool is currently disabled.
          </Text>
        </View>
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

          </>
        )}
      </ScrollView>
      {file && password && (
        <View style={s.modalActions}>
          <Pressable style={[s.btnPrimary, { opacity: 0.4 }]} onPress={handleProtect} disabled accessibilityRole="button" accessibilityLabel="Protection unavailable">
            <Text style={s.btnPrimaryText}>Protection Unavailable</Text>
          </Pressable>
        </View>
      )}
    </View>
  );
}
