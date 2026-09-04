import React, { useState, useEffect } from 'react';
import {
  View,
  Text,
  TextInput,
  TouchableOpacity,
  Modal,
  StyleSheet,
  KeyboardAvoidingView,
  Platform,
  ActivityIndicator,
  Pressable,
  ScrollView,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Clipboard from 'expo-clipboard';
import * as Haptics from 'expo-haptics';
import { useAppTheme } from '../../hooks/useAppTheme';
import { font, radius, space } from '../../styles/theme';

interface SendTextModalProps {
  visible: boolean;
  onClose: () => void;
  onSend: (text: string) => Promise<void>;
  targetDeviceName?: string;
  isSending?: boolean;
}

export default function SendTextModal({
  visible,
  onClose,
  onSend,
  targetDeviceName = 'PC',
  isSending = false,
}: SendTextModalProps) {
  const { colors, shadows } = useAppTheme();
  const [text, setText] = useState('');
  const [isFocused, setIsFocused] = useState(false);

  useEffect(() => {
    if (visible) {
      setText('');
      setIsFocused(false);
    }
  }, [visible]);

  const handlePasteClipboard = async () => {
    try {
      const hasString = await Clipboard.hasStringAsync();
      if (hasString) {
        const clipText = await Clipboard.getStringAsync();
        if (clipText) {
          Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
          setText((prev) => (prev ? `${prev}\n${clipText}` : clipText));
        }
      }
    } catch (e) {
      console.warn('Failed to read clipboard:', e);
    }
  };

  const handleSend = async () => {
    const trimmed = text.trim();
    if (!trimmed || isSending) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    try {
      await onSend(trimmed);
      onClose();
    } catch (err) {
      // Handled by parent
    }
  };

  return (
    <Modal
      visible={visible}
      transparent
      animationType="slide"
      onRequestClose={onClose}
    >
      <KeyboardAvoidingView
        behavior={Platform.OS === 'ios' ? 'padding' : 'height'}
        keyboardVerticalOffset={Platform.OS === 'ios' ? 40 : 0}
        style={styles.overlay}
      >
        <Pressable style={styles.backdrop} onPress={onClose} />

        <ScrollView
          contentContainerStyle={{ flexGrow: 0 }}
          keyboardShouldPersistTaps="handled"
          bounces={false}
          showsVerticalScrollIndicator={false}
        >
          <View
            style={[
              styles.container,
              {
                backgroundColor: colors.bg.elevated,
                borderColor: colors.border.subtle,
                ...shadows.elevated,
              },
            ]}
          >
          {/* Drag Handle */}
          <View style={[styles.handle, { backgroundColor: colors.border.medium }]} />

          {/* Header */}
          <View style={styles.header}>
            <View style={{ flex: 1 }}>
              <Text style={[styles.title, { color: colors.text.primary }]}>
                Send Text to {targetDeviceName}
              </Text>
              <Text style={[styles.subtitle, { color: colors.text.tertiary }]}>
                Transfers instantly to your PC clipboard
              </Text>
            </View>
            <TouchableOpacity
              style={[styles.closeBtn, { backgroundColor: colors.border.subtle }]}
              onPress={onClose}
              hitSlop={{ top: 10, bottom: 10, left: 10, right: 10 }}
            >
              <Ionicons name="close" size={20} color={colors.text.secondary} />
            </TouchableOpacity>
          </View>

          {/* Text Input Area */}
          <View
            style={[
              styles.inputWrapper,
              {
                backgroundColor: colors.bg.input,
                borderColor: isFocused ? colors.accent.primary : colors.border.subtle,
              },
            ]}
          >
            <TextInput
              style={[styles.textInput, { color: colors.text.primary }]}
              placeholder="Type or paste text to send to your PC..."
              placeholderTextColor={colors.text.tertiary}
              multiline
              textAlignVertical="top"
              value={text}
              onChangeText={setText}
              onFocus={() => setIsFocused(true)}
              onBlur={() => setIsFocused(false)}
              autoFocus
              maxLength={10000}
            />
          </View>

          {/* Quick Helper Chips */}
          <View style={styles.helperRow}>
            <TouchableOpacity
              style={[styles.chip, { backgroundColor: colors.bg.card, borderColor: colors.border.subtle }]}
              onPress={handlePasteClipboard}
              activeOpacity={0.7}
            >
              <Ionicons name="clipboard-outline" size={15} color={colors.accent.primary} />
              <Text style={[styles.chipText, { color: colors.text.primary }]}>Paste Clipboard</Text>
            </TouchableOpacity>

            {text.length > 0 && (
              <TouchableOpacity
                style={[styles.chip, { backgroundColor: colors.bg.card, borderColor: colors.border.subtle }]}
                onPress={() => {
                  Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
                  setText('');
                }}
                activeOpacity={0.7}
              >
                <Ionicons name="trash-outline" size={15} color={colors.accent.error} />
                <Text style={[styles.chipText, { color: colors.accent.error }]}>Clear</Text>
              </TouchableOpacity>
            )}

            <View style={{ flex: 1 }} />
            <Text style={[styles.charCount, { color: colors.text.tertiary }]}>
              {text.length} chars
            </Text>
          </View>

          {/* Send Button */}
          <TouchableOpacity
            style={[
              styles.sendBtn,
              {
                backgroundColor: text.trim().length > 0 ? colors.accent.primary : colors.border.medium,
                opacity: text.trim().length > 0 && !isSending ? 1 : 0.6,
              },
            ]}
            onPress={handleSend}
            disabled={text.trim().length === 0 || isSending}
            activeOpacity={0.8}
          >
            {isSending ? (
              <ActivityIndicator size="small" color="#fff" />
            ) : (
              <>
                <Ionicons name="paper-plane" size={18} color="#fff" style={{ marginRight: 8 }} />
                <Text style={styles.sendBtnText}>Send to PC</Text>
              </>
            )}
          </TouchableOpacity>
        </View>
        </ScrollView>
      </KeyboardAvoidingView>
    </Modal>
  );
}

const styles = StyleSheet.create({
  overlay: {
    flex: 1,
    justifyContent: 'flex-end',
  },
  backdrop: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: 'rgba(0,0,0,0.6)',
  },
  container: {
    borderTopLeftRadius: 28,
    borderTopRightRadius: 28,
    borderWidth: 1,
    borderBottomWidth: 0,
    padding: space.xl,
    paddingBottom: Platform.OS === 'ios' ? 36 : 24,
  },
  handle: {
    width: 38,
    height: 4,
    borderRadius: 2,
    alignSelf: 'center',
    marginBottom: 16,
  },
  header: {
    flexDirection: 'row',
    alignItems: 'center',
    marginBottom: 16,
  },
  title: {
    fontFamily: font.bold,
    fontSize: 18,
    letterSpacing: -0.3,
  },
  subtitle: {
    fontFamily: font.regular,
    fontSize: 13,
    marginTop: 2,
  },
  closeBtn: {
    width: 32,
    height: 32,
    borderRadius: 16,
    alignItems: 'center',
    justifyContent: 'center',
  },
  inputWrapper: {
    borderRadius: radius.lg,
    borderWidth: 1.5,
    minHeight: 120,
    maxHeight: 200,
    padding: space.md,
    marginBottom: 12,
  },
  textInput: {
    fontFamily: font.regular,
    fontSize: 15,
    lineHeight: 22,
    flex: 1,
  },
  helperRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 8,
    marginBottom: 18,
  },
  chip: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 6,
    paddingHorizontal: 12,
    paddingVertical: 7,
    borderRadius: radius.pill,
    borderWidth: 1,
  },
  chipText: {
    fontFamily: font.medium,
    fontSize: 12,
  },
  charCount: {
    fontFamily: font.regular,
    fontSize: 12,
  },
  sendBtn: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    height: 52,
    borderRadius: radius.lg,
  },
  sendBtnText: {
    fontFamily: font.bold,
    fontSize: 16,
    color: '#fff',
  },
});
