/**
 * share-receiver.tsx — Share Intent Receiver (Popup Style)
 * ──────────────────────────────────────────────────────────
 * When FlyShelf is selected from the Android share sheet, this screen
 * appears as a translucent popup with two actions:
 *
 *   1. 🔒 Vault  — opens a naming/category sub-modal, then encrypts & stores
 *   2. 📤 Send   — immediately sends to paired PC devices
 *
 * Flow:
 *   Share sheet → FlyShelf → popup (Vault / Send)
 *                                ↓ (if Vault)
 *                           name + category picker → encrypt → done
 */

import React, { useState, useEffect, useMemo, useCallback } from 'react';
import {
  View, Text, StyleSheet, TouchableOpacity, TextInput,
  ActivityIndicator, NativeModules, KeyboardAvoidingView, Platform,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as FileSystem from 'expo-file-system/legacy';
import { router } from 'expo-router';
import { useAppTheme } from '../hooks/useAppTheme';
import { font, space, radius } from '../styles/theme';
import AppErrorBoundary from '../components/AppErrorBoundary';
import { useVault } from '../features/vault/useVault';
import { VaultCategory } from '../features/vault/vaultTypes';
import { useSettings } from '../context/SettingsContext';
import { toast } from '../context/ToastContext';
import { resolveBestPcUrl } from '../utils/networkHelpers';

const { ShareIntent } = NativeModules;

interface SharedFile {
  uri: string;
  mimeType: string;
  fileName: string;
}

// ─── Popup steps ───
type Step = 'choose' | 'vault-details' | 'sending' | 'done';

function ShareReceiverInner() {
  const { colors, shadows } = useAppTheme();
  const s = useMemo(() => createStyles(colors), [colors]);
  const { manifest, addFile } = useVault();
  const { pairingKey, pairedDevices, pcLocalIp, deviceName } = useSettings();

  const [sharedFiles, setSharedFiles] = useState<SharedFile[]>([]);
  const [sharedText, setSharedText] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [step, setStep] = useState<Step>('choose');

  // Vault sub-step state
  const [fileName, setFileName] = useState('');
  const [selectedCategory, setSelectedCategory] = useState<VaultCategory | null>(null);
  const [isSaving, setIsSaving] = useState(false);

  // Send state
  const [isSending, setIsSending] = useState(false);

  // ─── Read shared intent ───
  useEffect(() => {
    (async () => {
      try {
        if (!ShareIntent) { setIsLoading(false); return; }
        const result = await ShareIntent.getSharedFiles();
        if (result) {
          if (result.files?.length > 0) {
            setSharedFiles(result.files);
            // Pre-fill name from first file
            setFileName(result.files[0].fileName?.replace(/\.[^.]+$/, '') || 'Shared File');
          }
          if (result.text) setSharedText(result.text);
        }
      } catch {}
      setIsLoading(false);
    })();
    return () => { ShareIntent?.clearIntent?.(); };
  }, []);

  // Pre-select first category
  useEffect(() => {
    if (manifest?.categories?.length && !selectedCategory) {
      setSelectedCategory(manifest.categories[0]);
    }
  }, [manifest]);

  // ─── Close ───
  const close = () => {
    ShareIntent?.clearIntent?.();
    router.replace('/(tabs)' as any);
  };

  // ─── Save to Vault ───
  const handleVaultSave = useCallback(async () => {
    if (!selectedCategory) { toast.error('Pick a category'); return; }
    setIsSaving(true);
    try {
      for (const file of sharedFiles) {
        const tempName = `share_${Date.now()}_${file.fileName.replace(/[^a-zA-Z0-9.-]/g, '_')}`;
        const tempUri = `${FileSystem.cacheDirectory}${tempName}`;
        await FileSystem.copyAsync({ from: file.uri, to: tempUri });
        const info = await FileSystem.getInfoAsync(tempUri);

        // Build final name: user-chosen name + original extension
        const ext = file.fileName.includes('.') ? '.' + file.fileName.split('.').pop() : '';
        const finalName = sharedFiles.length === 1
          ? `${fileName.trim() || 'Untitled'}${ext}`
          : file.fileName;

        await addFile(tempUri, finalName, file.mimeType, selectedCategory.id, (info as any).size || 0);
        try { await FileSystem.deleteAsync(tempUri, { idempotent: true }); } catch {}
      }

      // Handle shared text as a text file
      if (sharedText && !sharedFiles.length) {
        const textFile = `${FileSystem.cacheDirectory}shared_text_${Date.now()}.txt`;
        await FileSystem.writeAsStringAsync(textFile, sharedText);
        const finalName = `${fileName.trim() || 'Shared Text'}.txt`;
        await addFile(textFile, finalName, 'text/plain', selectedCategory.id, sharedText.length);
        try { await FileSystem.deleteAsync(textFile, { idempotent: true }); } catch {}
      }

      toast.success('Saved to Vault 🔒');
      setStep('done');
      setTimeout(close, 800);
    } catch (e: any) {
      toast.error('Save Failed', e?.message || 'Could not encrypt file');
    }
    setIsSaving(false);
  }, [sharedFiles, sharedText, fileName, selectedCategory, addFile]);

  // ─── Send to PC ───
  const handleSend = useCallback(async () => {
    if (!pairingKey) {
      toast.error('Not Paired', 'Pair with a PC first in Settings');
      return;
    }
    const pcUrl = resolveBestPcUrl(pairedDevices, pcLocalIp);
    if (!pcUrl) {
      toast.error('PC Offline', 'No online PC found. Open FlyShelf on your PC.');
      return;
    }

    setStep('sending');
    setIsSending(true);
    try {
      let sent = 0;
      for (const file of sharedFiles) {
        const tempName = `send_${Date.now()}_${file.fileName.replace(/[^a-zA-Z0-9.-]/g, '_')}`;
        const tempUri = `${FileSystem.cacheDirectory}${tempName}`;
        await FileSystem.copyAsync({ from: file.uri, to: tempUri });

        const res = await FileSystem.uploadAsync(`${pcUrl}/api/archive_upload`, tempUri, {
          httpMethod: 'POST',
          uploadType: FileSystem.FileSystemUploadType.BINARY_CONTENT,
          headers: {
            'X-FlyShelf-Client': 'MobileCompanion',
            'X-Pairing-Key': pairingKey,
            'X-Original-Date': Date.now().toString(),
            'X-File-Name': encodeURIComponent(file.fileName),
            'X-Batch-Name': encodeURIComponent(`SharedFrom_${deviceName || 'Android'}`),
            'X-Source-Device': deviceName || 'Android',
          },
        });
        if (res.status === 200) sent++;
        try { await FileSystem.deleteAsync(tempUri, { idempotent: true }); } catch {}
      }

      // Text → clipboard sync
      if (sharedText && !sharedFiles.length) {
        try {
          const r = await fetch(`${pcUrl}/api/clipboard`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'X-FlyShelf-Client': 'MobileCompanion', 'X-Pairing-Key': pairingKey },
            body: JSON.stringify({ text: sharedText, source: deviceName || 'Android', timestamp: Date.now() }),
          });
          if (r.ok) sent++;
        } catch {}
      }

      if (sent > 0) toast.success(`Sent to PC ✅`, `${sent} item(s) transferred`);
      else toast.error('Send Failed', 'Could not reach PC');

      setStep('done');
      setTimeout(close, 800);
    } catch (e: any) {
      toast.error('Transfer Failed', e?.message || 'Could not send');
      setStep('choose');
    }
    setIsSending(false);
  }, [sharedFiles, sharedText, pairingKey, pairedDevices, pcLocalIp, deviceName]);

  // ─── Loading state ───
  if (isLoading) {
    return (
      <View style={s.overlay}>
        <View style={s.popup}>
          <ActivityIndicator size="small" color={colors.accent.primary} />
          <Text style={s.loadingText}>Reading…</Text>
        </View>
      </View>
    );
  }

  const itemCount = sharedFiles.length + (sharedText && !sharedFiles.length ? 1 : 0);
  const previewLabel = sharedFiles.length > 0
    ? (sharedFiles.length === 1 ? sharedFiles[0].fileName : `${sharedFiles.length} files`)
    : (sharedText ? `"${sharedText.slice(0, 50)}${sharedText.length > 50 ? '…' : ''}"` : 'Nothing shared');

  // ─── STEP: Choose action ───
  if (step === 'choose') {
    return (
      <TouchableOpacity style={s.overlay} activeOpacity={1} onPress={close}>
        <TouchableOpacity style={s.popup} activeOpacity={1} onPress={() => {}}>
          {/* Preview badge */}
          <View style={s.previewRow}>
            <View style={[s.previewIcon, { backgroundColor: colors.accent.primaryDim }]}>
              <Ionicons name={sharedFiles.length > 0 ? 'document' : 'text'} size={18} color={colors.accent.primary} />
            </View>
            <View style={{ flex: 1, marginLeft: 10 }}>
              <Text style={s.previewName} numberOfLines={1}>{previewLabel}</Text>
              <Text style={s.previewMeta}>{itemCount} item{itemCount !== 1 ? 's' : ''} received</Text>
            </View>
          </View>

          <View style={s.divider} />

          {/* Vault option */}
          <TouchableOpacity style={s.optionRow} onPress={() => setStep('vault-details')} activeOpacity={0.7}>
            <View style={[s.optionIcon, { backgroundColor: `${colors.accent.primary}15` }]}>
              <Ionicons name="lock-closed" size={20} color={colors.accent.primary} />
            </View>
            <View style={{ flex: 1, marginLeft: 14 }}>
              <Text style={s.optionTitle}>Save to Vault</Text>
              <Text style={s.optionSub}>Encrypt & store securely on device</Text>
            </View>
            <Ionicons name="chevron-forward" size={16} color={colors.text.tertiary} />
          </TouchableOpacity>

          {/* Send option */}
          <TouchableOpacity style={s.optionRow} onPress={handleSend} activeOpacity={0.7}>
            <View style={[s.optionIcon, { backgroundColor: `${colors.accent.success}15` }]}>
              <Ionicons name="desktop-outline" size={20} color={colors.accent.success} />
            </View>
            <View style={{ flex: 1, marginLeft: 14 }}>
              <Text style={s.optionTitle}>Send to PC</Text>
              <Text style={s.optionSub}>Transfer to paired computer now</Text>
            </View>
            <Ionicons name="send" size={14} color={colors.text.tertiary} />
          </TouchableOpacity>

          {/* Cancel */}
          <TouchableOpacity style={s.cancelBtn} onPress={close} activeOpacity={0.7}>
            <Text style={s.cancelText}>Cancel</Text>
          </TouchableOpacity>
        </TouchableOpacity>
      </TouchableOpacity>
    );
  }

  // ─── STEP: Vault details (name + category) ───
  if (step === 'vault-details') {
    return (
      <TouchableOpacity style={s.overlay} activeOpacity={1} onPress={close}>
        <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : undefined} style={{ width: '100%', alignItems: 'center' }}>
          <TouchableOpacity style={s.popup} activeOpacity={1} onPress={() => {}}>
            {/* Header */}
            <View style={{ flexDirection: 'row', alignItems: 'center', marginBottom: 16 }}>
              <TouchableOpacity onPress={() => setStep('choose')} style={{ padding: 4, marginRight: 10 }}>
                <Ionicons name="chevron-back" size={20} color={colors.text.secondary} />
              </TouchableOpacity>
              <Text style={s.sectionTitle}>Save to Vault</Text>
            </View>

            {/* File name input */}
            <Text style={s.fieldLabel}>NAME</Text>
            <TextInput
              value={fileName}
              onChangeText={setFileName}
              placeholder="Enter a name…"
              placeholderTextColor={colors.text.tertiary}
              style={s.nameInput}
              autoFocus
              selectTextOnFocus
            />

            {/* Category picker */}
            <Text style={[s.fieldLabel, { marginTop: 16 }]}>CATEGORY</Text>
            <View style={s.catGrid}>
              {manifest?.categories.map(cat => (
                <TouchableOpacity
                  key={cat.id}
                  style={[
                    s.catChip,
                    selectedCategory?.id === cat.id && {
                      borderColor: cat.color,
                      backgroundColor: `${cat.color}18`,
                    },
                  ]}
                  onPress={() => setSelectedCategory(cat)}
                  activeOpacity={0.7}
                >
                  <Text style={{ fontSize: 15 }}>{cat.icon}</Text>
                  <Text style={[
                    s.catChipText,
                    selectedCategory?.id === cat.id && { color: cat.color, fontFamily: font.bold },
                  ]}>
                    {cat.name}
                  </Text>
                </TouchableOpacity>
              ))}
            </View>

            {/* Save button */}
            <TouchableOpacity
              style={[s.saveBtn, !selectedCategory && { opacity: 0.4 }]}
              onPress={handleVaultSave}
              disabled={isSaving || !selectedCategory}
              activeOpacity={0.8}
            >
              {isSaving ? (
                <ActivityIndicator size="small" color="#FFF" />
              ) : (
                <>
                  <Ionicons name="lock-closed" size={16} color="#FFF" />
                  <Text style={s.saveBtnText}>Encrypt & Save</Text>
                </>
              )}
            </TouchableOpacity>
          </TouchableOpacity>
        </KeyboardAvoidingView>
      </TouchableOpacity>
    );
  }

  // ─── STEP: Sending / Done ───
  return (
    <View style={s.overlay}>
      <View style={s.popup}>
        {step === 'sending' ? (
          <>
            <ActivityIndicator size="small" color={colors.accent.success} />
            <Text style={[s.loadingText, { marginTop: 12 }]}>Sending to PC…</Text>
          </>
        ) : (
          <>
            <Ionicons name="checkmark-circle" size={40} color={colors.accent.success} />
            <Text style={[s.loadingText, { marginTop: 8, color: colors.accent.success }]}>Done!</Text>
          </>
        )}
      </View>
    </View>
  );
}

export default function ShareReceiverScreen() {
  return (
    <AppErrorBoundary fallbackTitle="Share receiver crashed">
      <ShareReceiverInner />
    </AppErrorBoundary>
  );
}

// ─── Styles ───
const createStyles = (c: any) => StyleSheet.create({
  overlay: {
    flex: 1,
    backgroundColor: 'rgba(0,0,0,0.6)',
    justifyContent: 'center',
    alignItems: 'center',
    paddingHorizontal: 24,
  },
  popup: {
    width: '100%',
    maxWidth: 380,
    backgroundColor: c.bg.elevated,
    borderRadius: 24,
    padding: 20,
    borderWidth: 1,
    borderColor: c.border.medium,
  },
  loadingText: {
    color: c.text.secondary,
    fontSize: 13,
    fontFamily: font.medium,
    textAlign: 'center',
    marginTop: 8,
  },
  previewRow: {
    flexDirection: 'row',
    alignItems: 'center',
    marginBottom: 4,
  },
  previewIcon: {
    width: 40,
    height: 40,
    borderRadius: 12,
    alignItems: 'center',
    justifyContent: 'center',
  },
  previewName: {
    color: c.text.primary,
    fontSize: 14,
    fontFamily: font.semibold,
  },
  previewMeta: {
    color: c.text.tertiary,
    fontSize: 11,
    fontFamily: font.medium,
    marginTop: 1,
  },
  divider: {
    height: 1,
    backgroundColor: c.border.subtle,
    marginVertical: 14,
  },
  optionRow: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingVertical: 14,
    paddingHorizontal: 4,
  },
  optionIcon: {
    width: 42,
    height: 42,
    borderRadius: 12,
    alignItems: 'center',
    justifyContent: 'center',
  },
  optionTitle: {
    color: c.text.primary,
    fontSize: 15,
    fontFamily: font.bold,
  },
  optionSub: {
    color: c.text.tertiary,
    fontSize: 11,
    fontFamily: font.medium,
    marginTop: 1,
  },
  cancelBtn: {
    alignItems: 'center',
    paddingVertical: 14,
    marginTop: 6,
    borderTopWidth: 1,
    borderTopColor: c.border.subtle,
  },
  cancelText: {
    color: c.text.secondary,
    fontSize: 14,
    fontFamily: font.semibold,
  },
  // ─── Vault details step ───
  sectionTitle: {
    color: c.text.primary,
    fontSize: 17,
    fontFamily: font.bold,
  },
  fieldLabel: {
    color: c.text.tertiary,
    fontSize: 10,
    fontFamily: font.bold,
    letterSpacing: 1.2,
    marginBottom: 8,
  },
  nameInput: {
    backgroundColor: c.bg.input,
    borderRadius: radius.md,
    paddingHorizontal: 14,
    paddingVertical: 12,
    color: c.text.primary,
    fontSize: 15,
    fontFamily: font.medium,
    borderWidth: 1,
    borderColor: c.border.subtle,
  },
  catGrid: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: 8,
    marginBottom: 20,
  },
  catChip: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 6,
    paddingHorizontal: 12,
    paddingVertical: 9,
    borderRadius: 50,
    backgroundColor: c.bg.input,
    borderWidth: 1.5,
    borderColor: c.border.subtle,
  },
  catChipText: {
    color: c.text.secondary,
    fontSize: 12,
    fontFamily: font.semibold,
  },
  saveBtn: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 8,
    backgroundColor: c.accent.primary,
    borderRadius: radius.lg,
    paddingVertical: 14,
  },
  saveBtnText: {
    color: '#FFF',
    fontSize: 15,
    fontFamily: font.bold,
  },
});
