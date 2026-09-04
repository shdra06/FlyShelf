/**
 * share-receiver.tsx — All-In-One Quick Share & Store Hub
 * ──────────────────────────────────────────────────────────
 * High-efficiency, compact popup when sharing from Android.
 * Everything is visible in one unified view:
 *   1. Compact preview & inline editable name
 *   2. ⚡ 1-Tap Send to Paired Devices (All or specific device)
 *   3. 📦 1-Tap Save to Vault Categories (instant save without sub-modals)
 *   4. 📋 Quick Clipboard / Note actions
 */

import React, { useState, useEffect, useMemo, useCallback } from 'react';
import {
  View, Text, StyleSheet, TouchableOpacity, TextInput,
  ActivityIndicator, NativeModules, KeyboardAvoidingView, Platform,
  ScrollView, Image,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as FileSystem from 'expo-file-system/legacy';
import * as Haptics from 'expo-haptics';
import * as Clipboard from 'expo-clipboard';
import { router } from 'expo-router';
import { useAppTheme } from '../hooks/useAppTheme';
import { font, radius } from '../styles/theme';
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
  size?: number;
}

function ShareReceiverInner() {
  const { colors } = useAppTheme();
  const s = useMemo(() => createStyles(colors), [colors]);
  const { manifest, addFile } = useVault();
  const { pairingKey, pairedDevices, pcLocalIp, deviceName } = useSettings();

  const [sharedFiles, setSharedFiles] = useState<SharedFile[]>([]);
  const [sharedText, setSharedText] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  // Editable file / note title
  const [customTitle, setCustomTitle] = useState('');
  const [activeActionId, setActiveActionId] = useState<string | null>(null);
  const [successActionId, setSuccessActionId] = useState<string | null>(null);

  // Read intent payload
  useEffect(() => {
    (async () => {
      try {
        if (!ShareIntent || typeof ShareIntent.getSharedFiles !== 'function') {
          setIsLoading(false);
          return;
        }
        const result = await ShareIntent.getSharedFiles();
        if (result) {
          if (result.files?.length > 0) {
            setSharedFiles(result.files);
            const initialName = result.files[0].fileName?.replace(/\.[^.]+$/, '') || 'Shared File';
            setCustomTitle(initialName);
          }
          if (result.text) {
            setSharedText(result.text);
            if (!result.files?.length) {
              const preview = result.text.trim().slice(0, 32).replace(/\n/g, ' ');
              setCustomTitle(preview || 'Shared Note');
            }
          }
        }
      } catch (e: any) {
        console.warn('ShareIntent read error:', e);
      }
      setIsLoading(false);
    })();

    return () => {
      try { ShareIntent?.clearIntent?.(); } catch {}
    };
  }, []);

  const close = () => {
    try { ShareIntent?.clearIntent?.(); } catch {}
    router.replace('/(tabs)' as any);
  };

  const safeHaptic = (type: 'impact' | 'success' | 'error' = 'impact') => {
    try {
      if (type === 'success') Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
      else if (type === 'error') Haptics.notificationAsync(Haptics.NotificationFeedbackType.Error);
      else Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    } catch {}
  };

  // ─── 1-Tap Save to Vault Category ───
  const handleSaveToVault = useCallback(async (category: VaultCategory) => {
    if (activeActionId) return;
    setActiveActionId(`vault-${category.id}`);
    safeHaptic('impact');

    try {
      const titleToUse = customTitle.trim() || 'Shared File';

      if (sharedFiles.length > 0) {
        for (const file of sharedFiles) {
          const tempName = `share_${Date.now()}_${file.fileName.replace(/[^a-zA-Z0-9.-]/g, '_')}`;
          const tempUri = `${FileSystem.cacheDirectory}${tempName}`;
          await FileSystem.copyAsync({ from: file.uri, to: tempUri });
          const info = await FileSystem.getInfoAsync(tempUri);

          const ext = file.fileName.includes('.') ? '.' + file.fileName.split('.').pop() : '';
          const finalName = sharedFiles.length === 1
            ? `${titleToUse}${ext}`
            : file.fileName;

          await addFile(tempUri, finalName, file.mimeType, category.id, (info as any).size || file.size || 0);
          try { await FileSystem.deleteAsync(tempUri, { idempotent: true }); } catch {}
        }
      } else if (sharedText) {
        const textFile = `${FileSystem.cacheDirectory}shared_text_${Date.now()}.txt`;
        await FileSystem.writeAsStringAsync(textFile, sharedText);
        const finalName = `${titleToUse}.txt`;
        await addFile(textFile, finalName, 'text/plain', category.id, sharedText.length);
        try { await FileSystem.deleteAsync(textFile, { idempotent: true }); } catch {}
      }

      setSuccessActionId(`vault-${category.id}`);
      safeHaptic('success');
      toast.success(`Saved to ${category.name} 🔒`);
      setTimeout(close, 700);
    } catch (e: any) {
      safeHaptic('error');
      toast.error('Save Failed', e?.message || 'Could not save to Vault');
      setActiveActionId(null);
    }
  }, [sharedFiles, sharedText, customTitle, addFile, activeActionId]);

  // ─── 1-Tap Send to Specific PC or All Devices ───
  const handleSendToDevice = useCallback(async (targetDevice?: any) => {
    if (activeActionId) return;
    const actionKey = targetDevice ? `device-${targetDevice.deviceId || targetDevice.deviceName}` : 'device-all';
    setActiveActionId(actionKey);
    safeHaptic('impact');

    try {
      if (!pairingKey) {
        toast.error('Not Paired', 'Pair with a PC in FlyShelf Settings first.');
        setActiveActionId(null);
        return;
      }

      const candidateUrls: string[] = [];
      if (targetDevice) {
        const url = resolveBestPcUrl([targetDevice], pcLocalIp);
        if (url) candidateUrls.push(url);
      } else {
        // Broadcast to all known paired devices
        pairedDevices.forEach(d => {
          const u = resolveBestPcUrl([d], pcLocalIp);
          if (u && !candidateUrls.includes(u)) candidateUrls.push(u);
        });
        if (candidateUrls.length === 0 && pcLocalIp) {
          candidateUrls.push(`http://${pcLocalIp}:8765`);
        }
      }

      if (candidateUrls.length === 0) {
        toast.error('PC Offline', 'No active PC connection found. Make sure FlyShelf is open on PC.');
        setActiveActionId(null);
        return;
      }

      let sentCount = 0;
      const titleToUse = customTitle.trim() || 'Shared';

      for (const targetUrl of candidateUrls) {
        // Send files
        for (const file of sharedFiles) {
          const tempName = `send_${Date.now()}_${file.fileName.replace(/[^a-zA-Z0-9.-]/g, '_')}`;
          const tempUri = `${FileSystem.cacheDirectory}${tempName}`;
          await FileSystem.copyAsync({ from: file.uri, to: tempUri });

          const ext = file.fileName.includes('.') ? '.' + file.fileName.split('.').pop() : '';
          const finalName = sharedFiles.length === 1 ? `${titleToUse}${ext}` : file.fileName;

          const res = await FileSystem.uploadAsync(`${targetUrl}/api/archive_upload`, tempUri, {
            httpMethod: 'POST',
            uploadType: FileSystem.FileSystemUploadType.BINARY_CONTENT,
            headers: {
              'X-FlyShelf-Client': 'MobileCompanion',
              'X-Pairing-Key': pairingKey,
              'X-Original-Date': Date.now().toString(),
              'X-File-Name': encodeURIComponent(finalName),
              'X-Batch-Name': encodeURIComponent(`SharedFrom_${deviceName || 'Android'}`),
              'X-Source-Device': deviceName || 'Android',
            },
          });
          if (res.status === 200) sentCount++;
          try { await FileSystem.deleteAsync(tempUri, { idempotent: true }); } catch {}
        }

        // Send text
        if (sharedText && !sharedFiles.length) {
          try {
            const r = await fetch(`${targetUrl}/api/clipboard`, {
              method: 'POST',
              headers: {
                'Content-Type': 'application/json',
                'X-FlyShelf-Client': 'MobileCompanion',
                'X-Pairing-Key': pairingKey,
              },
              body: JSON.stringify({
                text: sharedText,
                source: deviceName || 'Android',
                timestamp: Date.now(),
              }),
            });
            if (r.ok) sentCount++;
          } catch {}
        }
      }

      if (sentCount > 0) {
        setSuccessActionId(actionKey);
        safeHaptic('success');
        toast.success('Sent Successfully ⚡', `${sentCount} item(s) transferred`);
        setTimeout(close, 700);
      } else {
        safeHaptic('error');
        toast.error('Send Failed', 'Could not reach target device');
        setActiveActionId(null);
      }
    } catch (e: any) {
      safeHaptic('error');
      toast.error('Transfer Error', e?.message || 'Failed to send file');
      setActiveActionId(null);
    }
  }, [sharedFiles, sharedText, customTitle, pairingKey, pairedDevices, pcLocalIp, deviceName, activeActionId]);

  // ─── Quick Copy to Clipboard ───
  const handleQuickCopy = async () => {
    safeHaptic('impact');
    if (sharedText) {
      await Clipboard.setStringAsync(sharedText);
      toast.clipboard('Copied to Clipboard 📋', sharedText);
      setTimeout(close, 500);
    } else if (sharedFiles[0]?.uri) {
      try {
        const b64 = await FileSystem.readAsStringAsync(sharedFiles[0].uri, {
          encoding: FileSystem.EncodingType.Base64,
        });
        await Clipboard.setImageAsync(b64);
        toast.success('Image Copied to Clipboard 📋');
        setTimeout(close, 500);
      } catch {
        toast.info('File Path Available', sharedFiles[0].fileName);
      }
    }
  };

  if (isLoading) {
    return (
      <View style={s.overlay}>
        <View style={s.card}>
          <ActivityIndicator size="small" color={colors.accent.primary} />
          <Text style={s.loadingText}>Reading shared content…</Text>
        </View>
      </View>
    );
  }

  const isImage = sharedFiles[0]?.mimeType?.startsWith('image/') || false;
  const isDoc = sharedFiles[0]?.mimeType?.includes('pdf') || sharedFiles[0]?.mimeType?.includes('word') || sharedFiles[0]?.mimeType?.includes('sheet');
  const previewUri = isImage ? sharedFiles[0]?.uri : null;
  const totalItems = sharedFiles.length > 0 ? sharedFiles.length : (sharedText ? 1 : 0);

  // Available categories with fallback defaults
  const categories: VaultCategory[] = manifest?.categories?.length ? manifest.categories : [
    { id: 'cat-docs', name: 'Documents', icon: '📄', color: '#4A62EB', fileCount: 0 },
    { id: 'cat-media', name: 'Media', icon: '🖼', color: '#10B981', fileCount: 0 },
    { id: 'cat-work', name: 'Work', icon: '💼', color: '#F59E0B', fileCount: 0 },
    { id: 'cat-finance', name: 'Finance', icon: '💳', color: '#8B5CF6', fileCount: 0 },
    { id: 'cat-personal', name: 'Personal', icon: '🔐', color: '#EC4899', fileCount: 0 },
    { id: 'cat-general', name: 'General', icon: '📦', color: '#6B7280', fileCount: 0 },
  ];

  return (
    <View style={s.overlay}>
      <KeyboardAvoidingView
        behavior={Platform.OS === 'ios' ? 'padding' : undefined}
        style={s.keyboardWrap}
      >
        <View style={s.card}>
          {/* Header */}
          <View style={s.headerRow}>
            <View style={s.headerBadge}>
              <Ionicons name="share-social" size={16} color={colors.accent.primary} />
              <Text style={s.headerTitle}>Share & Store</Text>
            </View>
            <TouchableOpacity onPress={close} style={s.closeBtn} hitSlop={10}>
              <Ionicons name="close" size={20} color={colors.text.secondary} />
            </TouchableOpacity>
          </View>

          <ScrollView
            showsVerticalScrollIndicator={false}
            keyboardShouldPersistTaps="handled"
            contentContainerStyle={s.scrollContent}
          >
            {/* Live Preview & Editable Title */}
            <View style={s.previewBox}>
              {previewUri ? (
                <Image source={{ uri: previewUri }} style={s.thumbImage} resizeMode="cover" />
              ) : (
                <View style={[s.thumbIconWrap, { backgroundColor: `${colors.accent.primary}18` }]}>
                  <Ionicons
                    name={isDoc ? 'document-text' : (sharedFiles.length > 0 ? 'folder' : 'chatbox-ellipses')}
                    size={22}
                    color={colors.accent.primary}
                  />
                </View>
              )}

              <View style={s.previewDetails}>
                <TextInput
                  value={customTitle}
                  onChangeText={setCustomTitle}
                  placeholder="Item name…"
                  placeholderTextColor={colors.text.tertiary}
                  style={s.titleInput}
                  numberOfLines={1}
                />
                <View style={s.metaRow}>
                  <Text style={s.metaText}>
                    {sharedFiles.length > 0
                      ? `${totalItems} file${totalItems > 1 ? 's' : ''} • ${sharedFiles[0]?.mimeType?.split('/')[1] || 'binary'}`
                      : `${sharedText?.length || 0} characters • Text`}
                  </Text>
                  <TouchableOpacity onPress={handleQuickCopy} style={s.miniCopyBtn}>
                    <Ionicons name="copy-outline" size={12} color={colors.accent.primary} />
                    <Text style={s.miniCopyText}>Copy</Text>
                  </TouchableOpacity>
                </View>
              </View>
            </View>

            {/* SECTION 1: ⚡ SEND TO DEVICES */}
            <View style={s.sectionWrap}>
              <View style={s.sectionHeader}>
                <Ionicons name="paper-plane" size={14} color={colors.accent.primary} />
                <Text style={s.sectionLabel}>SEND TO DEVICES</Text>
                {pairedDevices.length > 0 && (
                  <Text style={s.sectionHint}>{pairedDevices.length} paired</Text>
                )}
              </View>

              <View style={s.deviceChipGrid}>
                {/* Broadcast to All */}
                <TouchableOpacity
                  style={[
                    s.deviceChip,
                    s.deviceChipAll,
                    activeActionId === 'device-all' && s.chipActive,
                    successActionId === 'device-all' && s.chipSuccess,
                  ]}
                  onPress={() => handleSendToDevice()}
                  activeOpacity={0.7}
                  disabled={Boolean(activeActionId)}
                >
                  {activeActionId === 'device-all' ? (
                    <ActivityIndicator size="small" color="#FFF" />
                  ) : successActionId === 'device-all' ? (
                    <Ionicons name="checkmark-circle" size={16} color="#FFF" />
                  ) : (
                    <Ionicons name="radio" size={16} color="#FFF" />
                  )}
                  <Text style={s.deviceChipAllText}>
                    {successActionId === 'device-all' ? 'Sent to All!' : 'All Devices'}
                  </Text>
                </TouchableOpacity>

                {/* Individual Paired Devices */}
                {pairedDevices.map((dev, idx) => {
                  const devKey = `device-${dev.deviceId || dev.deviceName || idx}`;
                  const isActive = activeActionId === devKey;
                  const isSuccess = successActionId === devKey;

                  return (
                    <TouchableOpacity
                      key={devKey}
                      style={[
                        s.deviceChip,
                        isActive && s.chipActive,
                        isSuccess && s.chipSuccess,
                      ]}
                      onPress={() => handleSendToDevice(dev)}
                      activeOpacity={0.7}
                      disabled={Boolean(activeActionId)}
                    >
                      {isActive ? (
                        <ActivityIndicator size="small" color={colors.accent.primary} />
                      ) : isSuccess ? (
                        <Ionicons name="checkmark-circle" size={16} color={colors.accent.success} />
                      ) : (
                        <Ionicons
                          name={dev.deviceType === 'Mobile' ? 'phone-portrait-outline' : 'desktop-outline'}
                          size={15}
                          color={colors.text.primary}
                        />
                      )}
                      <Text style={s.deviceChipText} numberOfLines={1}>
                        {isSuccess ? 'Sent!' : (dev.deviceName || 'PC')}
                      </Text>
                      <View style={[s.onlineDot, { backgroundColor: colors.accent.success }]} />
                    </TouchableOpacity>
                  );
                })}

                {pairedDevices.length === 0 && (
                  <TouchableOpacity
                    style={s.pairPromptBtn}
                    onPress={() => {
                      close();
                      router.push('/(tabs)/settings' as any);
                    }}
                  >
                    <Ionicons name="link-outline" size={14} color={colors.text.tertiary} />
                    <Text style={s.pairPromptText}>No PC paired yet • Tap to connect</Text>
                  </TouchableOpacity>
                )}
              </View>
            </View>

            {/* SECTION 2: 📦 1-TAP SAVE TO VAULT */}
            <View style={s.sectionWrap}>
              <View style={s.sectionHeader}>
                <Ionicons name="cube-outline" size={14} color={colors.accent.info || '#8B5CF6'} />
                <Text style={s.sectionLabel}>STORE IN VAULT (1-TAP)</Text>
              </View>

              <View style={s.vaultChipGrid}>
                {categories.map((cat) => {
                  const catKey = `vault-${cat.id}`;
                  const isActive = activeActionId === catKey;
                  const isSuccess = successActionId === catKey;

                  return (
                    <TouchableOpacity
                      key={cat.id}
                      style={[
                        s.vaultChip,
                        { borderColor: `${cat.color || colors.accent.primary}40` },
                        isActive && { backgroundColor: `${cat.color || colors.accent.primary}25` },
                        isSuccess && { backgroundColor: colors.accent.success, borderColor: colors.accent.success },
                      ]}
                      onPress={() => handleSaveToVault(cat)}
                      activeOpacity={0.7}
                      disabled={Boolean(activeActionId)}
                    >
                      {isActive ? (
                        <ActivityIndicator size="small" color={cat.color || colors.accent.primary} />
                      ) : isSuccess ? (
                        <Ionicons name="checkmark-circle" size={15} color="#FFF" />
                      ) : (
                        <Text style={s.vaultChipIcon}>{cat.icon || '📁'}</Text>
                      )}
                      <Text
                        style={[
                          s.vaultChipText,
                          isSuccess && { color: '#FFF', fontFamily: font.bold },
                        ]}
                        numberOfLines={1}
                      >
                        {isSuccess ? 'Saved!' : cat.name}
                      </Text>
                    </TouchableOpacity>
                  );
                })}
              </View>
            </View>
          </ScrollView>

          {/* Bottom dismiss button */}
          <TouchableOpacity style={s.cancelBar} onPress={close} activeOpacity={0.7}>
            <Text style={s.cancelBarText}>Dismiss</Text>
          </TouchableOpacity>
        </View>
      </KeyboardAvoidingView>
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
    backgroundColor: 'rgba(0,0,0,0.72)',
    justifyContent: 'center',
    alignItems: 'center',
    paddingHorizontal: 16,
  },
  keyboardWrap: {
    width: '100%',
    maxWidth: 420,
  },
  card: {
    backgroundColor: c.bg.elevated,
    borderRadius: 24,
    padding: 16,
    borderWidth: 1,
    borderColor: c.border.medium,
    maxHeight: '90%',
    elevation: 10,
    shadowColor: '#000',
    shadowOffset: { width: 0, height: 8 },
    shadowOpacity: 0.35,
    shadowRadius: 16,
  },
  scrollContent: {
    paddingBottom: 8,
  },
  loadingText: {
    color: c.text.secondary,
    fontSize: 13,
    fontFamily: font.medium,
    textAlign: 'center',
    marginTop: 12,
  },
  headerRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    marginBottom: 12,
  },
  headerBadge: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 6,
    backgroundColor: `${c.accent.primary}18`,
    paddingHorizontal: 10,
    paddingVertical: 4,
    borderRadius: 20,
  },
  headerTitle: {
    color: c.accent.primary,
    fontSize: 13,
    fontFamily: font.bold,
    letterSpacing: 0.3,
  },
  closeBtn: {
    width: 32,
    height: 32,
    borderRadius: 16,
    backgroundColor: c.bg.input,
    alignItems: 'center',
    justifyContent: 'center',
  },
  previewBox: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: c.bg.input,
    borderRadius: radius.md,
    padding: 10,
    marginBottom: 14,
    borderWidth: 1,
    borderColor: c.border.subtle,
  },
  thumbImage: {
    width: 44,
    height: 44,
    borderRadius: 8,
    backgroundColor: c.bg.secondary,
  },
  thumbIconWrap: {
    width: 44,
    height: 44,
    borderRadius: 8,
    alignItems: 'center',
    justifyContent: 'center',
  },
  previewDetails: {
    flex: 1,
    marginLeft: 10,
  },
  titleInput: {
    color: c.text.primary,
    fontSize: 14,
    fontFamily: font.semibold,
    paddingVertical: 2,
    paddingHorizontal: 0,
    borderBottomWidth: 1,
    borderBottomColor: c.border.subtle,
  },
  metaRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    marginTop: 4,
  },
  metaText: {
    color: c.text.tertiary,
    fontSize: 11,
    fontFamily: font.medium,
  },
  miniCopyBtn: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 3,
    paddingHorizontal: 6,
    paddingVertical: 2,
    borderRadius: 6,
    backgroundColor: `${c.accent.primary}15`,
  },
  miniCopyText: {
    color: c.accent.primary,
    fontSize: 10,
    fontFamily: font.bold,
  },
  sectionWrap: {
    marginBottom: 14,
  },
  sectionHeader: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 6,
    marginBottom: 8,
  },
  sectionLabel: {
    color: c.text.tertiary,
    fontSize: 11,
    fontFamily: font.bold,
    letterSpacing: 0.8,
  },
  sectionHint: {
    color: c.text.tertiary,
    fontSize: 10,
    fontFamily: font.medium,
    marginLeft: 'auto',
  },
  deviceChipGrid: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: 8,
  },
  deviceChip: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 6,
    backgroundColor: c.bg.input,
    paddingHorizontal: 12,
    paddingVertical: 9,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: c.border.subtle,
  },
  deviceChipAll: {
    backgroundColor: c.accent.primary,
    borderColor: c.accent.primary,
  },
  deviceChipAllText: {
    color: '#FFF',
    fontSize: 12,
    fontFamily: font.bold,
  },
  deviceChipText: {
    color: c.text.primary,
    fontSize: 12,
    fontFamily: font.semibold,
    maxWidth: 120,
  },
  onlineDot: {
    width: 6,
    height: 6,
    borderRadius: 3,
    marginLeft: 2,
  },
  chipActive: {
    opacity: 0.85,
    transform: [{ scale: 0.98 }],
  },
  chipSuccess: {
    backgroundColor: c.accent.success,
    borderColor: c.accent.success,
  },
  pairPromptBtn: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 6,
    paddingVertical: 8,
    paddingHorizontal: 12,
    borderRadius: 10,
    borderWidth: 1,
    borderStyle: 'dashed',
    borderColor: c.border.medium,
    width: '100%',
    justifyContent: 'center',
  },
  pairPromptText: {
    color: c.text.secondary,
    fontSize: 12,
    fontFamily: font.medium,
  },
  vaultChipGrid: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: 8,
  },
  vaultChip: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 6,
    paddingHorizontal: 12,
    paddingVertical: 9,
    borderRadius: 12,
    backgroundColor: c.bg.input,
    borderWidth: 1.2,
  },
  vaultChipIcon: {
    fontSize: 13,
  },
  vaultChipText: {
    color: c.text.primary,
    fontSize: 12,
    fontFamily: font.semibold,
  },
  cancelBar: {
    alignItems: 'center',
    justifyContent: 'center',
    paddingVertical: 10,
    marginTop: 4,
    borderTopWidth: 1,
    borderTopColor: c.border.subtle,
  },
  cancelBarText: {
    color: c.text.tertiary,
    fontSize: 13,
    fontFamily: font.medium,
  },
});

