import React, { useState, useEffect, useRef, useCallback, useMemo } from 'react';
import AppErrorBoundary from '../../components/AppErrorBoundary';
import { StyleSheet, Text, View, TouchableOpacity, SafeAreaView, ActivityIndicator, useWindowDimensions, Modal, Alert, ScrollView, Image, Platform, FlatList, ToastAndroid, Linking, TextInput, Pressable } from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import * as Sharing from 'expo-sharing';
import * as IntentLauncher from 'expo-intent-launcher';
import * as MediaLibrary from 'expo-media-library';
import * as FileSystem from 'expo-file-system/legacy';
import * as DocumentPicker from 'expo-document-picker';
import DateTimePicker from '@react-native-community/datetimepicker';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { useSettings } from '../../context/SettingsContext';
import { font, radius, space, component } from '../../styles/theme';
import { useAppTheme } from '../../hooks/useAppTheme';
import AnimatedCard from '../../components/AnimatedCard';
import AnimatedPressable from '../../components/AnimatedPressable';
import { resolveBestPcUrl } from '../../utils/networkHelpers';
import { router } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import Animated, { useSharedValue, useAnimatedScrollHandler } from 'react-native-reanimated';
import ScreenHeader from '../../components/ScreenHeader';
import {
  useArchiveSync,
  MediaAsset, FirebaseDevice, DeviceGroup, SourceFilter,
} from '../../features/archive/useArchiveSync';

const getThumbSize = (w: number) => (w - 50) / 4;


function FilesScreenInner() {
  const { colors, shadows } = useAppTheme();
  const s = useMemo(() => createStyles(colors, shadows), [colors, shadows]);
  const { pcLocalIp, deviceName, defaultTargetDeviceName, pairingKey, pairedDevices } = useSettings();
  const { width: screenWidth } = useWindowDimensions();
  const THUMB_SIZE = getThumbSize(screenWidth);

  // ─── Sync hook (Firebase listeners + media scan + upload pipeline) ───
  const {
    allFirebaseDevices,
    deviceGroups,
    saveGroupToFirebase,
    deleteGroupFromFirebase,
    hasPermission,
    mediaAssets, setMediaAssets,
    isScanning,
    scanMedia: scanMediaHook,
    autoScan,
    hasScannedRef,
    isUploading,
    isPausedRef,
    isCancelledRef,
    uploadIndex,
    uploadTotal,
    uploadProgress, setUploadProgress,
    executeTransfer,
    pauseTransfer,
    cancelTransfer,
  } = useArchiveSync();

  // Date range — default 1 year (so images/videos load broadly)
  const [startDate, setStartDate] = useState(new Date(Date.now() - 365 * 24 * 60 * 60 * 1000));
  const [endDate, setEndDate] = useState(new Date());
  const [showStartPicker, setShowStartPicker] = useState(false);
  const [showEndPicker, setShowEndPicker] = useState(false);

  // Wrap scanMedia so the UI can pass current date range
  const scanMedia = useCallback(() => {
    scanMediaHook(startDate, endDate);
  }, [scanMediaHook, startDate, endDate]);

  // Transfer pause state (mirrored for UI)
  const [isPaused, setIsPaused] = useState(false);

  // Media UI state
  const [activeTab, setActiveTab] = useState<'Images'|'Videos'|'PDFs'|'Docs'|'All'>('All');
  const [sourceFilter, setSourceFilter] = useState<SourceFilter>('All');
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [enlargedPreview, setEnlargedPreview] = useState<MediaAsset | null>(null);

  // Files browser state
  const [fileSearchText, setFileSearchText] = useState('');
  const [browserFiles, setBrowserFiles] = useState<MediaAsset[]>([]);

  // Device & group UI state
  const [selectedTarget, setSelectedTarget] = useState<FirebaseDevice | null>(null);
  const [showGroupModal, setShowGroupModal] = useState(false);
  const [editingGroup, setEditingGroup] = useState<DeviceGroup | null>(null);
  const [newGroupName, setNewGroupName] = useState('');
  const [selectedGroupDevices, setSelectedGroupDevices] = useState<Set<string>>(new Set());
  const [urlPopup, setUrlPopup] = useState<{ device: FirebaseDevice; localUrl: string; globalUrl: string } | null>(null);

  // Auto-scan when permission is available and haven't scanned yet
  useEffect(() => {
    autoScan(startDate, endDate);
  }, [hasPermission]);

  const saveGroupHandler = async (group: DeviceGroup) => {
    await saveGroupToFirebase(group);
  };

  const createOrUpdateGroup = async () => {
    if (!newGroupName.trim()) { Alert.alert('Error', 'Group name is required'); return; }
    const deviceNames = Array.from(selectedGroupDevices);
    if (deviceNames.length === 0) { Alert.alert('Error', 'Select at least one device'); return; }
    const groupId = editingGroup ? editingGroup.id : `grp_${Date.now()}`;
    await saveGroupToFirebase({ id: groupId, name: newGroupName.trim(), deviceNames });
    setShowGroupModal(false);
    setEditingGroup(null);
    setNewGroupName('');
    setSelectedGroupDevices(new Set());
    if (Platform.OS === 'android') ToastAndroid.show(`Group "${newGroupName.trim()}" saved`, ToastAndroid.SHORT);
  };

  const deleteGroup = (groupId: string) => {
    Alert.alert('Delete Group', 'Are you sure?', [
      { text: 'Cancel' },
      { text: 'Delete', style: 'destructive', onPress: async () => {
        await deleteGroupFromFirebase(groupId);
      }}
    ]);
  };

  // ─── Browse Android files ───
  const browseFiles = async () => {
    try {
      const result = await DocumentPicker.getDocumentAsync({ multiple: true, copyToCacheDirectory: true });
      if (!result.canceled && result.assets && result.assets.length > 0) {
        const newFiles = result.assets.map((f: any) => ({
          id: `browse_${f.uri}_${Date.now()}`,
          uri: f.uri,
          filename: f.name || 'file',
          creationTime: Date.now(),
          mediaType: f.mimeType?.includes('image') ? 'photo' : f.mimeType?.includes('video') ? 'video' : f.mimeType?.includes('pdf') ? 'pdf' : 'doc',
          source: 'Browse' as any,
          fileSize: f.size || 0,
        }));
        setBrowserFiles(prev => [...prev, ...newFiles]);
        // Auto-select browsed files
        setSelectedIds(prev => {
          const updated = new Set(prev);
          newFiles.forEach((f: any) => updated.add(f.id));
          return updated;
        });
        if (Platform.OS === 'android') ToastAndroid.show(`Added ${newFiles.length} file(s)`, ToastAndroid.SHORT);
      }
    } catch (err) {
      Alert.alert('Browse Failed', 'Could not open file picker.');
    }
  };

  // ─── Build date-range folder name ───
  const buildBatchName = () => {
    const sender = deviceName || 'Mobile';
    const from = startDate.toLocaleDateString('en-US', { month: 'short', day: 'numeric' }).replace(/\s/g, '');
    const to = endDate.toLocaleDateString('en-US', { month: 'short', day: 'numeric' }).replace(/\s/g, '');
    return `${sender}_${from}_to_${to}`;
  };

  /**
   * handleTransfer — resolves relay routing then delegates to useArchiveSync.executeTransfer.
   * This keeps URL-resolution logic in the UI layer (where pairedDevices is available)
   * while the actual upload pipeline lives in the hook.
   */
  const handleTransfer = async (targetNode: any) => {
    if (!pairingKey) { Alert.alert('Error', 'No pairing key configured.'); return; }
    const allItems = [...getFilteredAssets(), ...browserFiles];
    const targetQueue = allItems.filter(a => selectedIds.has(a.id));

    if (targetQueue.length === 0) {
      Alert.alert('No Items', 'Select items to send first.');
      return;
    }

    // Find a PC relay if target has no direct route
    let resolvedUrl = targetNode.resolvedUrl;
    let useRelay = false;

    if (targetNode.connectionType === 'sync-only' || !resolvedUrl) {
      // Find any PC with Cloudflare to relay through
      const relayPC = pairedDevices.find(d => d.deviceType === 'PC' && d.isOnline && d.globalUrl);
      if (relayPC) {
        resolvedUrl = relayPC.localUrl || relayPC.globalUrl;
        useRelay = true;
        if (Platform.OS === 'android') ToastAndroid.show(`📡 Relaying via ${relayPC.deviceName}`, ToastAndroid.SHORT);
      } else {
        Alert.alert('No Route Available', 'No PC with Cloudflare is online to relay files.\n\nEnsure at least one PC is running FlyShelf with internet access.');
        return;
      }
    }

    if (!resolvedUrl) {
      Alert.alert('Route Failed', 'No reachable URL for this device.');
      return;
    }

    setIsPaused(false);
    await executeTransfer(
      targetNode,
      targetQueue,
      buildBatchName(),
      useRelay,
      resolvedUrl,
      () => {
        // onComplete callback — reset selection
        setSelectedIds(new Set());
        setBrowserFiles([]);
      },
    );
  };

  const toggleSelection = (id: string) => {
    setSelectedIds(prev => {
      const updated = new Set(prev);
      updated.has(id) ? updated.delete(id) : updated.add(id);
      return updated;
    });
  };

  const toggleSelectAll = (items: any[]) => {
    setSelectedIds(prev => {
      const updated = new Set(prev);
      const allSelected = items.every(i => updated.has(i.id));
      items.forEach(i => allSelected ? updated.delete(i.id) : updated.add(i.id));
      return updated;
    });
  };

  const getFilteredAssets = () => {
    let items = mediaAssets;
    // Source filter
    if (sourceFilter !== 'All') {
      items = items.filter(a => a.source === sourceFilter);
    }
    // Type filter
    if (activeTab !== 'All') {
      items = items.filter(a => {
        if (activeTab === 'Images') return a.mediaType === 'photo';
        if (activeTab === 'Videos') return a.mediaType === 'video';
        if (activeTab === 'PDFs') return a.mediaType === 'pdf';
        if (activeTab === 'Docs') return a.mediaType === 'doc';
        return true;
      });
    }
    return items;
  };

  // ─── DEVICE CARD COMPONENT ───
  const DeviceCard = ({ device, type }: { device: any, type: 'local' | 'global' }) => {
    const isPC = device.DeviceType === 'PC';
    const hasCloudflare = device.connectionType === 'cloudflare';
    const isUnverifiedCloudflare = device.connectionType === 'cloudflare-unverified';
    const isSyncOnly = device.connectionType === 'sync-only';
    
    const getDeviceUrls = () => {
      let localUrl = '';
      let globalUrl = '';
      if (device.Url) {
        const candidates = device.Url.split(',').map((u: string) => u.trim()).filter((u: string) => u.startsWith('http'));
        localUrl = candidates.find((u: string) => !u.includes('trycloudflare.com')) || '';
      }
      if (device.GlobalUrl && device.GlobalUrl.includes('trycloudflare.com')) {
        globalUrl = device.GlobalUrl.endsWith('/') ? device.GlobalUrl.slice(0, -1) : device.GlobalUrl;
      }
      return { localUrl, globalUrl };
    };
    
    return (
      <TouchableOpacity 
        style={[s.deviceCard, { borderColor: type === 'local' ? '#10B98144' : '#3B82F644' }]}
        onPress={() => { setSelectedTarget(device); if (!isScanning && mediaAssets.length === 0) scanMedia(); }}
        onLongPress={() => {
          const { localUrl, globalUrl } = getDeviceUrls();
          if (localUrl || globalUrl) setUrlPopup({ device, localUrl, globalUrl });
          else { if (Platform.OS === 'android') ToastAndroid.show('No URLs available', ToastAndroid.SHORT); }
        }}
        activeOpacity={0.7}
        accessibilityLabel={`${device.DeviceName || 'Unknown'}, ${device.DeviceType}, ${type === 'local' ? 'local network' : 'cloud'}`}
        accessibilityRole="button"
        accessibilityHint="Tap to select as transfer target"
      >
        <View style={[s.deviceIcon, { backgroundColor: type === 'local' ? '#10B98118' : '#3B82F618' }]}>
          <Ionicons name={isPC ? "desktop-outline" : "phone-portrait-outline"} size={22} color={type === 'local' ? '#10B981' : '#3B82F6'} />
        </View>
        <View style={{ flex: 1, marginLeft: 12 }}>
          <Text style={s.deviceName}>{device.DeviceName || 'Unknown'}</Text>
          <View style={{ flexDirection: 'row', alignItems: 'center', gap: 6, marginTop: 3 }}>
            {type === 'local' && (
              <View style={[s.badge, { backgroundColor: '#10B98122' }]}>
                <Text style={[s.badgeText, { color: '#10B981' }]}>⚡ LAN</Text>
              </View>
            )}
            {hasCloudflare && (
              <View style={[s.badge, { backgroundColor: '#3B82F622' }]}>
                <Text style={[s.badgeText, { color: '#3B82F6' }]}>☁️ Cloudflare</Text>
              </View>
            )}
            {isSyncOnly && (
              <View style={[s.badge, { backgroundColor: '#F59E0B22' }]}>
                <Text style={[s.badgeText, { color: '#F59E0B' }]}>📡 Sync Only</Text>
              </View>
            )}
            <Text style={{ color: '#555', fontSize: 10 }}>{device.DeviceType}</Text>
          </View>
        </View>
        <Ionicons name="chevron-forward" size={16} color={colors.text.tertiary} />
      </TouchableOpacity>
    );
  };

  // ─── UPLOAD PROGRESS SCREEN ───
  if (isUploading) {
    const pct = uploadTotal > 0 ? Math.round((uploadIndex / uploadTotal) * 100) : 0;
    return (
      <LinearGradient colors={[colors.bg.base, colors.bg.baseEnd]} style={{ flex: 1 }}>
      <SafeAreaView style={s.container}>
        <View style={s.header}>
          <Text style={s.title}>Transferring...</Text>
          <Text style={s.subtitle}>{selectedTarget?.DeviceName || 'Device'}</Text>
        </View>
        <View style={s.card}>
          <Text style={{ color: colors.text.primary, fontSize: 16, fontFamily: font.bold, marginBottom: 6 }}>{uploadIndex} / {uploadTotal} files</Text>
          <Text style={{ color: colors.text.secondary, fontSize: 12, fontFamily: font.medium, marginBottom: 16 }}>{pct}% complete</Text>
          <View style={{ height: 8, backgroundColor: colors.border.subtle, borderRadius: 4, overflow: 'hidden', marginBottom: 24 }}>
            <View style={{ height: '100%', width: `${pct}%`, backgroundColor: colors.accent.success, borderRadius: 4 }} />
          </View>
          <View style={{ flexDirection: 'row', gap: 12 }}>
            <TouchableOpacity style={[s.controlBtn, { backgroundColor: isPaused ? colors.accent.success : colors.accent.warning, flex: 1 }]} onPress={() => { const next = pauseTransfer(); setIsPaused(next); }} accessibilityLabel={isPaused ? 'Resume transfer' : 'Pause transfer'} accessibilityRole="button">
              <Text style={s.controlBtnText}>{isPaused ? '▶ Resume' : '⏸ Pause'}</Text>
            </TouchableOpacity>
            <TouchableOpacity style={[s.controlBtn, { backgroundColor: colors.accent.error, flex: 1 }]} onPress={() => { cancelTransfer(); }} accessibilityLabel="Abort transfer" accessibilityRole="button">
              <Text style={s.controlBtnText}>✕ Abort</Text>
            </TouchableOpacity>
          </View>
        </View>
      </SafeAreaView>
      </LinearGradient>
    );
  }

  // ─── TRANSFER PANEL (after device selected) ───
  if (selectedTarget) {
    const filteredAssets = getFilteredAssets();
    const allDisplayItems = [...filteredAssets, ...browserFiles];

    return (
      <LinearGradient colors={[colors.bg.base, colors.bg.baseEnd]} style={{ flex: 1 }}>
      <SafeAreaView style={s.container}>
        {/* Header with prominent back */}
        <View style={[s.header, { flexDirection: 'row', alignItems: 'center', paddingTop: 50 }]}>
          <TouchableOpacity onPress={() => { setSelectedTarget(null); setMediaAssets([]); setBrowserFiles([]); setSelectedIds(new Set()); }} style={{ marginRight: 14, padding: 10, backgroundColor: '#EF4444', borderRadius: 12 }} accessibilityLabel="Go back" accessibilityRole="button">
            <Ionicons name="chevron-back" size={18} color="#FFF" />
          </TouchableOpacity>
          <View style={{ flex: 1 }}>
            <Text style={[s.title, { fontSize: 22 }]}>Send to {selectedTarget.DeviceName}</Text>
            <View style={{ flexDirection: 'row', alignItems: 'center', gap: 6, marginTop: 3 }}>
              <View style={{ width: 8, height: 8, borderRadius: 4, backgroundColor: selectedTarget.connectionType === 'local' ? '#10B981' : '#3B82F6' }} />
              <Text style={{ color: selectedTarget.connectionType === 'cloudflare-unverified' ? '#F59E0B' : '#8A8F98', fontSize: 11 }}>{selectedTarget.connectionType === 'local' ? 'Local Network' : selectedTarget.connectionType === 'cloudflare' ? 'Via Cloudflare' : selectedTarget.connectionType === 'cloudflare-unverified' ? '⚠️ Cloudflare (Unverified)' : 'Global Sync'}</Text>
            </View>
          </View>
        </View>

        {/* Date Range */}
        <View style={{ flexDirection: 'row', alignItems: 'center', paddingHorizontal: 20, marginBottom: 10, gap: 8 }}>
          <TouchableOpacity style={s.dateBtn} onPress={() => setShowStartPicker(true)} accessibilityLabel={`From date: ${startDate.toLocaleDateString()}`} accessibilityRole="button">
            <Text style={s.dateLabel}>FROM</Text>
            <Text style={s.dateValue}>{startDate.toLocaleDateString()}</Text>
          </TouchableOpacity>
          <TouchableOpacity style={s.dateBtn} onPress={() => setShowEndPicker(true)} accessibilityLabel={`To date: ${endDate.toLocaleDateString()}`} accessibilityRole="button">
            <Text style={s.dateLabel}>TO</Text>
            <Text style={s.dateValue}>{endDate.toLocaleDateString()}</Text>
          </TouchableOpacity>
          <TouchableOpacity style={{ backgroundColor: '#4A62EB', borderRadius: 12, padding: 12, paddingHorizontal: 16 }} onPress={scanMedia} accessibilityLabel="Scan media files" accessibilityRole="button">
            <Text style={{ color: '#FFF', fontSize: 13, fontWeight: '700' }}>Scan</Text>
          </TouchableOpacity>
        </View>
        {showStartPicker && <DateTimePicker value={startDate} mode="date" display="default" onChange={(e: any, d?: Date) => { setShowStartPicker(false); if (d) setStartDate(d); }} />}
        {showEndPicker && <DateTimePicker value={endDate} mode="date" display="default" onChange={(e: any, d?: Date) => { setShowEndPicker(false); if (d) setEndDate(d); }} />}

        {/* Source Filters */}
        <ScrollView horizontal showsHorizontalScrollIndicator={false} style={{ flexGrow: 0, flexShrink: 0 }} contentContainerStyle={{ paddingHorizontal: 20, gap: 6, marginBottom: 8 }}>
          {(['All', 'Camera', 'WhatsApp', 'Downloads'] as SourceFilter[]).map(src => (
            <TouchableOpacity key={src} style={[s.sourceChip, sourceFilter === src && s.sourceChipActive]} onPress={() => setSourceFilter(src)} accessibilityLabel={`Filter: ${src}${sourceFilter === src ? ', selected' : ''}`} accessibilityRole="tab">
              <Text style={[s.sourceChipText, sourceFilter === src && s.sourceChipTextActive]}>
                {src === 'Camera' ? '📷' : src === 'WhatsApp' ? '💬' : src === 'Downloads' ? '📂' : '🌐'} {src}
              </Text>
            </TouchableOpacity>
          ))}
          <TouchableOpacity style={[s.sourceChip, { backgroundColor: '#4A62EB33', borderColor: '#4A62EB' }]} onPress={browseFiles} accessibilityLabel="Browse Android files" accessibilityRole="button">
            <Text style={[s.sourceChipText, { color: '#4A62EB', fontWeight: '700' }]}>📁 Browse Android</Text>
          </TouchableOpacity>
        </ScrollView>

        {/* Type Tabs */}
        <View style={s.tabRow}>
          {(['All', 'Images', 'Videos', 'PDFs', 'Docs'] as const).map(t => {
            const count = mediaAssets.filter(a => {
              if (t === 'All') return true;
              if (t === 'Images') return a.mediaType === 'photo';
              if (t === 'Videos') return a.mediaType === 'video';
              if (t === 'PDFs') return a.mediaType === 'pdf';
              if (t === 'Docs') return a.mediaType === 'doc';
              return true;
            }).length;
            return (
              <TouchableOpacity key={t} style={[s.tab, activeTab === t && s.tabActive]} onPress={() => setActiveTab(t)} accessibilityLabel={`${t} tab, ${count} items${activeTab === t ? ', selected' : ''}`} accessibilityRole="tab">
                <Text style={[s.tabText, activeTab === t && s.tabTextActive]}>{t} ({count})</Text>
              </TouchableOpacity>
            );
          })}
        </View>

        {/* Count + Select All */}
        <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', paddingHorizontal: 20, paddingVertical: 8 }}>
          <Text style={{ color: '#8A8F98', fontSize: 12, fontWeight: '600' }}>
            {allDisplayItems.length} found{browserFiles.length > 0 ? ` (+${browserFiles.length} browsed)` : ''} · {selectedIds.size} selected
          </Text>
          <TouchableOpacity style={{ backgroundColor: '#2A2F3A', paddingHorizontal: 12, paddingVertical: 6, borderRadius: 8 }} onPress={() => toggleSelectAll(allDisplayItems)} accessibilityLabel="Select all files" accessibilityRole="button">
            <Text style={{ color: '#FFF', fontSize: 11, fontWeight: 'bold' }}>Select All</Text>
          </TouchableOpacity>
        </View>

        {/* Grid */}
        {isScanning ? (
          <ActivityIndicator size="large" color="#4A62EB" style={{ marginTop: 40 }} />
        ) : (
          <ScrollView contentContainerStyle={{ paddingHorizontal: 12, paddingBottom: 120 }}>
            <View style={{ flexDirection: 'row', flexWrap: 'wrap' }}>
              {allDisplayItems.map((asset, idx) => {
                const isSelected = selectedIds.has(asset.id);
                const isImage = asset.mediaType === 'photo' || asset.mediaType === 'video';
                return (
                  <TouchableOpacity key={idx} style={{ margin: 3, width: THUMB_SIZE, height: THUMB_SIZE }} onPress={() => toggleSelection(asset.id)} onLongPress={() => setEnlargedPreview(asset)} accessibilityLabel={`${asset.filename || 'File'}, ${isSelected ? 'selected' : 'not selected'}`} accessibilityRole="checkbox">
                    {isImage ? (
                      <Image source={{ uri: asset.uri }} style={{ width: '100%', height: '100%', borderRadius: 10, backgroundColor: '#2A2F3A' }} />
                    ) : (
                      <View style={{ width: '100%', height: '100%', borderRadius: 10, backgroundColor: '#1C1F26', alignItems: 'center', justifyContent: 'center', borderWidth: 1, borderColor: '#2A2F3A' }}>
                        <Ionicons name="document" size={24} color={asset.mediaType === 'pdf' ? '#EF4444' : '#3B82F6'} />
                        <Text style={{ color: '#AAA', fontSize: 8, marginTop: 4, paddingHorizontal: 4 }} numberOfLines={2}>{asset.filename}</Text>
                      </View>
                    )}
                    <TouchableOpacity style={[s.checkCircle, isSelected && s.checkCircleActive]} onPress={() => toggleSelection(asset.id)}>
                      {isSelected && <Ionicons name="checkmark" size={12} color="#FFF" />}
                    </TouchableOpacity>
                    {asset.mediaType === 'video' && (
                      <View style={{ position: 'absolute', bottom: 4, left: 4, backgroundColor: 'rgba(0,0,0,0.6)', paddingHorizontal: 5, paddingVertical: 2, borderRadius: 5 }}>
                        <Ionicons name="play" size={10} color="#FFF" />
                      </View>
                    )}
                    {asset.source === 'Browse' && (
                      <View style={{ position: 'absolute', top: 4, left: 4, backgroundColor: colors.accent.primary, paddingHorizontal: 4, paddingVertical: 1, borderRadius: 4 }}>
                        <Text style={{ color: colors.text.primary, fontSize: 7, fontWeight: 'bold' }}>FILE</Text>
                      </View>
                    )}
                  </TouchableOpacity>
                );
              })}
              {allDisplayItems.length === 0 && !isScanning && (
                <View style={{ width: '100%', alignItems: 'center', marginTop: 40 }}>
                  <Ionicons name="search" size={40} color={colors.text.tertiary} />
                  <Text style={{ color: colors.text.tertiary, marginTop: 12, fontSize: 14 }}>No files found. Try scanning or browsing.</Text>
                </View>
              )}
            </View>
          </ScrollView>
        )}

        {/* Send Button */}
        {selectedIds.size > 0 && (
          <View style={{ position: 'absolute', bottom: 20, left: 20, right: 20 }}>
            <TouchableOpacity style={s.sendButton} onPress={() => handleTransfer(selectedTarget)} activeOpacity={0.8} accessibilityLabel={`Send ${selectedIds.size} items to ${selectedTarget.DeviceName}`} accessibilityRole="button">
              <Text style={s.sendButtonText}>Send {selectedIds.size} Items to {selectedTarget.DeviceName}</Text>
              <Text style={{ color: colors.accent.success, fontSize: 10, marginTop: 3 }}>{buildBatchName()}</Text>
            </TouchableOpacity>
          </View>
        )}

        {/* Preview Modal */}
        <Modal visible={!!enlargedPreview} animationType="fade" transparent>
          <View style={[s.modalOverlay, { backgroundColor: 'rgba(0,0,0,0.95)' }]}>
            <TouchableOpacity style={{ position: 'absolute', top: 50, right: 20, zIndex: 10 }} onPress={() => setEnlargedPreview(null)} accessibilityLabel="Close preview" accessibilityRole="button">
              <View style={{ padding: 10, backgroundColor: colors.bg.cardHover, borderRadius: 20 }}>
                <Ionicons name="close" size={24} color={colors.text.primary} />
              </View>
            </TouchableOpacity>
            {enlargedPreview && (enlargedPreview.mediaType === 'photo' || enlargedPreview.mediaType === 'video') ? (
              <Image source={{ uri: enlargedPreview.uri }} style={{ width: '100%', height: '80%', resizeMode: 'contain' }} />
            ) : (
              <View style={{ alignItems: 'center' }}>
                <Ionicons name="document" size={80} color={colors.accent.warning} />
                <Text style={{ color: colors.text.primary, marginTop: 20, fontSize: 18, fontWeight: 'bold' }}>{enlargedPreview?.filename}</Text>
              </View>
            )}
          </View>
        </Modal>
      </SafeAreaView>
      </LinearGradient>
    );
  }

  // ─── File Actions ───
  const openFile = async (asset: any) => {
    try {
      const uri = asset.uri || asset.localUri;
      if (!uri) { Alert.alert('Error', 'No file path available'); return; }
      const fileUri = uri.startsWith('file://') ? uri : `file://${uri}`;
      if (Platform.OS === 'android') {
        // Copy to app-local cache first — getContentUriAsync only works on paths within the app's configured FileProvider roots
        const fileName = (asset.filename || uri.split('/').pop() || `file_${Date.now()}`).replace(/[^a-zA-Z0-9.-]/g, '_');
        const appLocalPath = `${(FileSystem as any).cacheDirectory}open_${fileName}`;
        try {
          await FileSystem.copyAsync({ from: fileUri, to: appLocalPath });
        } catch (copyErr) {
          // Try direct content URI as fallback for some Android versions
          try {
            await Sharing.shareAsync(fileUri);
            return;
          } catch { throw copyErr; }
        }
        const contentUri = await FileSystem.getContentUriAsync(appLocalPath);
        // Determine MIME type from extension
        const ext = fileName.split('.').pop()?.toLowerCase() || '';
        const mimeType = ext === 'pdf' ? 'application/pdf'
          : ext === 'doc' || ext === 'docx' ? 'application/msword'
          : ext === 'xls' || ext === 'xlsx' ? 'application/vnd.ms-excel'
          : ext === 'ppt' || ext === 'pptx' ? 'application/vnd.ms-powerpoint'
          : ext === 'txt' ? 'text/plain'
          : ext === 'apk' ? 'application/vnd.android.package-archive'
          : ext === 'zip' || ext === 'rar' ? 'application/zip'
          : ext === 'mp4' ? 'video/mp4'
          : ext === 'jpg' || ext === 'jpeg' ? 'image/jpeg'
          : ext === 'png' ? 'image/png'
          : '*/*';
        await IntentLauncher.startActivityAsync('android.intent.action.VIEW', {
          data: contentUri, flags: 1, type: mimeType,
        });
        // AM-8: Schedule cleanup of temp cache file after intent resolves (60s delay)
        setTimeout(() => FileSystem.deleteAsync(appLocalPath, { idempotent: true }).catch(() => {}), 60000);
      }
    } catch (e: any) {
      // Fallback: use share sheet
      try {
        const uri = asset.uri || asset.localUri;
        if (uri) await Sharing.shareAsync(uri.startsWith('file://') ? uri : `file://${uri}`);
      } catch (shareErr: any) {
        Alert.alert('Cannot Open', shareErr?.message || 'No app available to open this file.');
      }
    }
  };

  const shareFile = async (asset: any) => {
    try {
      const uri = asset.uri || asset.localUri;
      if (!uri) return;
      const fileUri = uri.startsWith('file://') ? uri : `file://${uri}`;
      await Sharing.shareAsync(fileUri);
    } catch (e: any) {
      if (Platform.OS === 'android') ToastAndroid.show('Share failed: ' + (e?.message || 'unknown error'), ToastAndroid.SHORT);
    }
  };



  const scrollY = useSharedValue(0);
  const scrollHandler = useAnimatedScrollHandler({ onScroll: (e) => { scrollY.value = e.contentOffset.y; } });

  const flatListData = useMemo(() => {
    let items = [...getFilteredAssets(), ...browserFiles];
    if (fileSearchText) items = items.filter(a => (a.filename || '').toLowerCase().includes(fileSearchText.toLowerCase()));
    return items;
  }, [mediaAssets, activeTab, sourceFilter, browserFiles, fileSearchText]);

  // ─── Connection status badge for header ───
  const archiveConnectionBadge = useMemo(() => {
    const pcUrl = resolveBestPcUrl(pairedDevices, pcLocalIp);
    if (pcUrl) {
      return pcUrl.includes('trycloudflare.com') ? '🟡 Cloud' : '🟢 LAN';
    }
    const hasOnlinePc = allFirebaseDevices.some(d => d.DeviceType === 'PC' && d.IsOnline);
    return hasOnlinePc ? '🟡 Cloud' : '⚪ Offline';
  }, [pairedDevices, pcLocalIp, allFirebaseDevices]);

  // ─── MAIN SCREEN: Files Browser ───
  return (
    <LinearGradient colors={[colors.bg.base, colors.bg.baseEnd]} style={{ flex: 1 }}>
    <View style={s.container}>
      <ScreenHeader
        title="Files"
        subtitle={`Documents & Media • ${archiveConnectionBadge}`}
        scrollY={scrollY}
        rightActions={
          <Pressable
            onPress={() => { Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); router.push('/pdf-tools' as any); }}
            style={{ padding: 8, borderRadius: 10, backgroundColor: colors.accent.primaryDim }}
            accessibilityLabel="PDF Tools"
            accessibilityRole="button"
          >
            <Ionicons name="document-text" size={22} color={colors.accent.primary} />
          </Pressable>
        }
      />

      {/* Search bar */}
      <View style={{ paddingHorizontal: 20, marginBottom: 12 }}>
        <View style={{ flexDirection: 'row', alignItems: 'center', backgroundColor: colors.bg.input, borderRadius: 12, paddingHorizontal: 14, borderWidth: 1, borderColor: colors.border.subtle }}>
          <Ionicons name="search" size={16} color={colors.text.tertiary} />
          <TextInput value={fileSearchText} onChangeText={setFileSearchText} placeholder="Search files..." placeholderTextColor={colors.text.tertiary} style={{ flex: 1, color: colors.text.primary, fontSize: 14, paddingVertical: 12, marginLeft: 10 }} accessibilityLabel="Search files" accessibilityRole="search" />
          {fileSearchText ? <TouchableOpacity onPress={() => setFileSearchText('')} accessibilityLabel="Clear search" accessibilityRole="button" hitSlop={{ top: 8, bottom: 8, left: 8, right: 8 }}><Ionicons name="close-circle" size={18} color={colors.text.tertiary} /></TouchableOpacity> : null}
        </View>
      </View>

      {/* Type Filter Chips */}
      <ScrollView horizontal showsHorizontalScrollIndicator={false} style={{ flexGrow: 0, flexShrink: 0, marginBottom: 10 }} contentContainerStyle={{ paddingHorizontal: 20, gap: 6, paddingVertical: 4 }}>
        {(['All', 'PDFs', 'Docs', 'Images', 'Videos'] as const).map(t => (
          <TouchableOpacity key={t} style={[s.sourceChip, activeTab === t && s.sourceChipActive]} onPress={() => setActiveTab(t)} accessibilityLabel={`${t} filter${activeTab === t ? ', selected' : ''}`} accessibilityRole="tab">
            <Text style={[s.sourceChipText, activeTab === t && s.sourceChipTextActive]}>
              {t === 'PDFs' ? '📄' : t === 'Docs' ? '📝' : t === 'Images' ? '🖼️' : t === 'Videos' ? '🎬' : '🌐'} {t}
            </Text>
          </TouchableOpacity>
        ))}
        <TouchableOpacity style={[s.sourceChip, { backgroundColor: colors.accent.primaryDim, borderColor: colors.accent.primary }]} onPress={browseFiles} accessibilityLabel="Browse files" accessibilityRole="button">
          <Text style={[s.sourceChipText, { color: colors.accent.primary, fontFamily: font.bold }]}>📁 Browse</Text>
        </TouchableOpacity>
        <TouchableOpacity style={[s.sourceChip, { backgroundColor: colors.accent.successDim, borderColor: colors.accent.success }]} onPress={scanMedia} accessibilityLabel="Refresh files" accessibilityRole="button">
          <Text style={[s.sourceChipText, { color: colors.accent.success, fontFamily: font.bold }]}>🔄 Refresh</Text>
        </TouchableOpacity>
      </ScrollView>

      {/* Count + Actions */}
      <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', paddingHorizontal: 20, paddingVertical: 6, marginBottom: 4 }}>
        <Text style={{ color: colors.text.secondary, fontSize: 12, fontFamily: font.semibold }}>
          {flatListData.length} files · {selectedIds.size} selected
        </Text>
        <TouchableOpacity style={{ backgroundColor: colors.bg.cardHover, paddingHorizontal: 10, paddingVertical: 5, borderRadius: 8, borderWidth: 1, borderColor: colors.border.subtle }} onPress={() => toggleSelectAll([...getFilteredAssets(), ...browserFiles])} accessibilityLabel="Select all files" accessibilityRole="button">
          <Text style={{ color: colors.text.primary, fontSize: 10, fontFamily: font.bold }}>Select All</Text>
        </TouchableOpacity>
      </View>

      {/* File List */}
      {isScanning ? (
        <View style={{ flex: 1, justifyContent: 'center', alignItems: 'center' }}>
          <ActivityIndicator size="large" color="#4A62EB" />
          <Text style={{ color: '#8A8F98', marginTop: 12, fontSize: 13 }}>Scanning device files...</Text>
        </View>
      ) : (
        <FlatList
          data={flatListData}
          onScroll={scrollHandler}
          keyExtractor={(item, idx) => item.id || `f_${idx}`}
          contentContainerStyle={{ paddingHorizontal: 16, paddingBottom: selectedIds.size > 0 ? 180 : 100 }}
          renderItem={({ item: asset }) => {
            const isSelected = selectedIds.has(asset.id);
            const isPdf = asset.mediaType === 'pdf';
            const isDoc = asset.mediaType === 'doc';
            const isVideo = asset.mediaType === 'video';
            const isImage = asset.mediaType === 'photo';
            const iconColor = isPdf ? colors.type.pdf : isDoc ? colors.type.doc : isVideo ? colors.type.video : colors.accent.success;
            return (
              <TouchableOpacity
                style={{ flexDirection: 'row', alignItems: 'center', backgroundColor: colors.bg.card, borderRadius: 14, padding: 12, marginBottom: 6, borderWidth: 1, borderColor: isSelected ? colors.accent.success : colors.border.subtle }}
                onPress={() => toggleSelection(asset.id)}
                onLongPress={() => isImage || isVideo ? setEnlargedPreview(asset) : openFile(asset)}
                accessibilityLabel={`${asset.filename || 'Unnamed'}, ${isPdf ? 'PDF' : isDoc ? 'DOC' : isVideo ? 'VIDEO' : 'IMAGE'}${isSelected ? ', selected' : ''}`}
                accessibilityRole="checkbox"
              >
                {isImage ? (
                  <Image source={{ uri: asset.uri }} style={{ width: 48, height: 48, borderRadius: 10, backgroundColor: colors.bg.cardHover }} />
                ) : (
                  <View style={{ width: 48, height: 48, borderRadius: 10, backgroundColor: `${iconColor}15`, alignItems: 'center', justifyContent: 'center' }}>
                    <Ionicons name={isPdf ? 'document' : isDoc ? 'document-text' : isVideo ? 'videocam' : 'image'} size={22} color={iconColor} />
                  </View>
                )}
                <View style={{ flex: 1, marginLeft: 12 }}>
                  <Text style={{ color: colors.text.primary, fontSize: 13, fontWeight: '600' }} numberOfLines={1}>{asset.filename || 'Unnamed'}</Text>
                  <View style={{ flexDirection: 'row', alignItems: 'center', gap: 8, marginTop: 3 }}>
                    <View style={[s.badge, { backgroundColor: `${iconColor}22` }]}>
                      <Text style={[s.badgeText, { color: iconColor }]}>{isPdf ? 'PDF' : isDoc ? 'DOC' : isVideo ? 'VIDEO' : 'IMAGE'}</Text>
                    </View>
                    {asset.fileSize ? <Text style={{ color: colors.text.tertiary, fontSize: 10 }}>{asset.fileSize > 1048576 ? `${(asset.fileSize / 1048576).toFixed(1)} MB` : `${Math.round(asset.fileSize / 1024)} KB`}</Text> : null}
                  </View>
                </View>
                <View style={{ flexDirection: 'row', gap: 4 }}>
                  {(isPdf || isDoc) && <TouchableOpacity onPress={() => openFile(asset)} style={{ padding: 8, backgroundColor: colors.accent.infoDim, borderRadius: 8 }} accessibilityLabel={`Open ${asset.filename || 'file'}`} accessibilityRole="button"><Ionicons name="open-outline" size={14} color={colors.accent.info} /></TouchableOpacity>}
                  <TouchableOpacity onPress={() => shareFile(asset)} style={{ padding: 8, backgroundColor: colors.accent.successDim, borderRadius: 8 }} accessibilityLabel={`Share ${asset.filename || 'file'}`} accessibilityRole="button"><Ionicons name="share-outline" size={14} color={colors.accent.success} /></TouchableOpacity>
                  <View style={[{ width: 24, height: 24, borderRadius: 12, borderWidth: 2, borderColor: isSelected ? colors.accent.success : colors.text.tertiary, alignItems: 'center', justifyContent: 'center' }, isSelected && { backgroundColor: colors.accent.success }]}>
                    {isSelected && <Ionicons name="checkmark" size={12} color="#FFF" />}
                  </View>
                </View>
              </TouchableOpacity>
            );
          }}
          ListEmptyComponent={<View style={{ alignItems: 'center', marginTop: 60 }}><Ionicons name="folder-open-outline" size={48} color={colors.text.tertiary} /><Text style={{ color: colors.text.secondary, marginTop: 12 }}>No files found</Text></View>}
        />
      )}

      {/* Bottom Action Bar */}
      {selectedIds.size > 0 && (
        <View style={{ position: 'absolute', bottom: 60, left: 0, right: 0, backgroundColor: colors.bg.base + 'EE', paddingHorizontal: 16, paddingVertical: 12, borderTopWidth: 1, borderTopColor: colors.border.subtle }}>
          <Text style={{ color: colors.text.secondary, fontSize: 11, marginBottom: 8, textAlign: 'center', fontFamily: font.medium }}>{selectedIds.size} file(s) selected</Text>
          <View style={{ flexDirection: 'row', gap: 8 }}>
            <TouchableOpacity style={{ flex: 1, backgroundColor: colors.accent.info, paddingVertical: 14, borderRadius: 14, alignItems: 'center' }} onPress={() => {
              const sel = flatListData.filter(a => selectedIds.has(a.id));
              if (sel.length === 1) shareFile(sel[0]);
              else Alert.alert('Share', 'Select a single file to share via Android.');
            }} accessibilityLabel="Share selected file" accessibilityRole="button">
              <Text style={{ color: colors.text.primary, fontSize: 13, fontWeight: '700', fontFamily: font.bold }}>📤 Share</Text>
            </TouchableOpacity>
            {(() => {
              const selPdfs = flatListData.filter(a => selectedIds.has(a.id) && (a.mediaType === 'pdf' || a.mediaType === 'doc'));
              return selPdfs.length >= 2 ? (
                <TouchableOpacity style={{ flex: 1, backgroundColor: colors.accent.warning, paddingVertical: 14, borderRadius: 14, alignItems: 'center' }} onPress={() => {
                  const pc = pairedDevices.find(d => d.deviceType === 'PC' && d.isOnline);
                  if (!pc) { Alert.alert('No PC', 'Connect to a PC to merge files.'); return; }
                  const target = { ...pc, resolvedUrl: pc.localUrl || pc.globalUrl, DeviceName: pc.deviceName };
                  setSelectedTarget(target);
                  handleTransfer(target);
                }} accessibilityLabel="Merge selected files on PC" accessibilityRole="button">
                  <Text style={{ color: colors.bg.base, fontSize: 13, fontWeight: '700', fontFamily: font.bold }}>📑 Merge on PC</Text>
                </TouchableOpacity>
              ) : null;
            })()}
            <TouchableOpacity style={{ flex: 1, backgroundColor: colors.accent.success, paddingVertical: 14, borderRadius: 14, alignItems: 'center' }} onPress={() => {
              const onlineDevs = pairedDevices.filter(d => d.isOnline);
              if (onlineDevs.length === 0) { Alert.alert('No Devices', 'Connect to a device first.'); return; }
              if (onlineDevs.length === 1) { 
                const target = { ...onlineDevs[0], resolvedUrl: onlineDevs[0].localUrl || onlineDevs[0].globalUrl, DeviceName: onlineDevs[0].deviceName };
                setSelectedTarget(target); 
                handleTransfer(target); 
                return; 
              }
              Alert.alert('Send to:', '', onlineDevs.map(d => ({ 
                text: `${d.deviceName} (${d.connectionType === 'LAN' ? 'LAN' : 'Cloud'})`, 
                onPress: () => { 
                  const target = { ...d, resolvedUrl: d.localUrl || d.globalUrl, DeviceName: d.deviceName };
                  setSelectedTarget(target); 
                  handleTransfer(target); 
                } 
              })).concat([{ text: 'Cancel' } as any]));
            }} accessibilityLabel={`Send ${selectedIds.size} files to device`} accessibilityRole="button">
              <Text style={{ color: colors.text.primary, fontSize: 13, fontWeight: '700', fontFamily: font.bold }}>📡 Send</Text>
            </TouchableOpacity>
          </View>
        </View>
      )}


      {/* AL-9: Duplicate Preview Modal removed — already rendered above (lines ~823-840) */}

      {/* URL Popup */}
      <Modal visible={!!urlPopup} transparent animationType="fade" onRequestClose={() => setUrlPopup(null)}>
        <TouchableOpacity style={{ flex: 1, backgroundColor: 'rgba(0,0,0,0.6)', justifyContent: 'center', alignItems: 'center' }} activeOpacity={1} onPress={() => setUrlPopup(null)}>
          <View style={{ backgroundColor: colors.bg.card, borderRadius: 20, padding: 24, width: '85%', borderWidth: 1, borderColor: colors.border.subtle }}>
            <Text style={{ color: colors.text.primary, fontSize: 18, fontWeight: '800', marginBottom: 16 }}>{urlPopup?.device?.DeviceName}</Text>
            {urlPopup?.localUrl ? (<View style={{ marginBottom: 16 }}><Text style={{ color: colors.accent.success, fontSize: 11, fontWeight: '700', marginBottom: 6 }}>⚡ LOCAL</Text><Text style={{ color: colors.text.secondary, fontSize: 12 }} selectable>{urlPopup.localUrl}</Text><TouchableOpacity onPress={() => { Linking.openURL(urlPopup!.localUrl); setUrlPopup(null); }} style={{ marginTop: 8, backgroundColor: colors.accent.success, padding: 10, borderRadius: 10, alignItems: 'center' }}><Text style={{ color: colors.text.primary, fontWeight: '700' }}>Open</Text></TouchableOpacity></View>) : null}
            {urlPopup?.globalUrl ? (<View><Text style={{ color: colors.accent.info, fontSize: 11, fontWeight: '700', marginBottom: 6 }}>☁️ GLOBAL</Text><Text style={{ color: colors.text.secondary, fontSize: 12 }} selectable>{urlPopup.globalUrl}</Text><TouchableOpacity onPress={() => { Linking.openURL(urlPopup!.globalUrl); setUrlPopup(null); }} style={{ marginTop: 8, backgroundColor: colors.accent.info, padding: 10, borderRadius: 10, alignItems: 'center' }}><Text style={{ color: colors.text.primary, fontWeight: '700' }}>Open</Text></TouchableOpacity></View>) : null}
          </View>
        </TouchableOpacity>
      </Modal>

      {/* Group Modal */}
      <Modal visible={showGroupModal} transparent animationType="slide" onRequestClose={() => setShowGroupModal(false)}>
        <View style={{ flex: 1, backgroundColor: 'rgba(0,0,0,0.7)', justifyContent: 'center', padding: 20 }}>
          <View style={{ backgroundColor: colors.bg.elevated, borderRadius: 20, padding: 20, maxHeight: '80%' }}>
            <Text style={{ color: colors.text.primary, fontSize: 18, fontWeight: '800', marginBottom: 16 }}>{editingGroup ? 'Edit Group' : 'Create Group'}</Text>
            <TextInput value={newGroupName} onChangeText={setNewGroupName} placeholder="Group name..." placeholderTextColor={colors.text.secondary} style={{ backgroundColor: colors.bg.base, borderRadius: 10, padding: 12, color: colors.text.primary, fontSize: 14, marginBottom: 16, borderWidth: 1, borderColor: colors.border.subtle }} />
            <ScrollView style={{ maxHeight: 250 }}>
              {pairedDevices.filter((d, i, a) => a.findIndex(x => x.deviceName === d.deviceName) === i).map((dev, i) => {
                const isSel = selectedGroupDevices.has(dev.deviceName);
                return (
                  <TouchableOpacity key={`gd_${i}`} onPress={() => { const n = new Set(selectedGroupDevices); isSel ? n.delete(dev.deviceName) : n.add(dev.deviceName); setSelectedGroupDevices(n); }} style={{ flexDirection: 'row', alignItems: 'center', padding: 12, backgroundColor: isSel ? colors.accent.warning + '15' : colors.bg.base, borderRadius: 10, marginBottom: 6, borderWidth: 1, borderColor: isSel ? colors.accent.warning + '44' : colors.bg.elevated }}>
                    <View style={{ width: 22, height: 22, borderRadius: 6, marginRight: 10, backgroundColor: isSel ? colors.accent.warning : colors.bg.cardHover, alignItems: 'center', justifyContent: 'center' }}>{isSel && <Text style={{ color: colors.bg.base, fontWeight: '900' }}>✓</Text>}</View>
                    <Text style={{ color: colors.text.primary, fontSize: 13, fontWeight: '600' }}>{dev.deviceName || 'Unknown'}</Text>
                  </TouchableOpacity>
                );
              })}
            </ScrollView>
            <View style={{ flexDirection: 'row', gap: 10, marginTop: 16 }}>
              <TouchableOpacity onPress={() => setShowGroupModal(false)} style={{ flex: 1, padding: 12, borderRadius: 10, backgroundColor: colors.bg.cardHover, alignItems: 'center' }}><Text style={{ color: colors.text.secondary, fontWeight: '700' }}>Cancel</Text></TouchableOpacity>
              <TouchableOpacity onPress={createOrUpdateGroup} style={{ flex: 1, padding: 12, borderRadius: 10, backgroundColor: colors.accent.warning, alignItems: 'center' }}><Text style={{ color: colors.bg.base, fontWeight: '800' }}>{editingGroup ? 'Save' : 'Create'}</Text></TouchableOpacity>
            </View>
          </View>
        </View>
      </Modal>
    </View>
    </LinearGradient>
  );
}

const createStyles = (c: any, sh: any) => StyleSheet.create({
  container: { flex: 1, backgroundColor: 'transparent' },
  header: { paddingTop: 60, paddingHorizontal: space['2xl'], marginBottom: space.lg },
  title: { fontSize: 30, fontFamily: font.extrabold, color: c.text.primary, letterSpacing: -0.8 },
  subtitle: { fontSize: 13, fontFamily: font.medium, color: c.text.tertiary, marginTop: 4, textTransform: 'uppercase', letterSpacing: 1.5 },
  sectionTitle: { color: c.text.primary, fontSize: 16, fontFamily: font.bold },
  card: { backgroundColor: c.bg.card, marginHorizontal: space.xl, borderRadius: radius.xl, padding: space['2xl'], borderWidth: 1, borderColor: c.border.subtle, borderTopColor: c.innerHighlight, marginTop: space.xl, ...sh.card },
  deviceCard: { flexDirection: 'row', alignItems: 'center', backgroundColor: c.bg.input, borderRadius: radius.lg, padding: 14, marginBottom: 10, borderWidth: 1, borderColor: c.border.subtle },
  deviceIcon: { width: 44, height: 44, borderRadius: 22, alignItems: 'center', justifyContent: 'center' },
  deviceName: { color: c.text.primary, fontSize: 15, fontFamily: font.bold },
  badge: { paddingHorizontal: 8, paddingVertical: 2, borderRadius: 6 },
  badgeText: { fontSize: 10, fontFamily: font.bold },
  emptyCard: { padding: space['2xl'], backgroundColor: c.bg.input, borderRadius: radius.lg, borderWidth: 1, borderColor: c.border.subtle, alignItems: 'center' },
  dateBtn: { flex: 1, backgroundColor: c.bg.card, borderRadius: radius.md, padding: 10, borderWidth: 1, borderColor: c.border.subtle },
  dateLabel: { color: c.text.tertiary, fontSize: 9, fontFamily: font.bold, marginBottom: 2 },
  dateValue: { color: c.text.primary, fontSize: 12, fontFamily: font.semibold },
  sourceChip: { 
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: c.bg.card, 
    borderRadius: radius.pill, 
    paddingHorizontal: 14, 
    paddingVertical: 8, 
    borderWidth: 1, 
    borderColor: c.border.subtle,
    minHeight: 38
  },
  sourceChipActive: { backgroundColor: c.accent.successDim, borderColor: c.accent.success },
  sourceChipText: { 
    color: c.text.secondary, 
    fontSize: 12, 
    fontFamily: font.semibold,
    textAlign: 'center',
    includeFontPadding: false
  },
  sourceChipTextActive: { color: c.accent.success },
  tabRow: { flexDirection: 'row', paddingHorizontal: space.xl, marginBottom: 6, gap: 4, flexWrap: 'wrap' },
  tab: { paddingVertical: 8, paddingHorizontal: 14, borderRadius: 10, backgroundColor: c.bg.card, minHeight: 36, justifyContent: 'center' },
  tabActive: { backgroundColor: c.accent.primary },
  tabText: { color: c.text.secondary, fontSize: 12, fontFamily: font.bold },
  tabTextActive: { color: c.text.primary },
  checkCircle: { position: 'absolute', top: 4, right: 4, width: 22, height: 22, borderRadius: 11, backgroundColor: 'rgba(0,0,0,0.5)', borderWidth: 2, borderColor: c.text.primary, alignItems: 'center', justifyContent: 'center' },
  checkCircleActive: { backgroundColor: c.accent.success },
  sendButton: { backgroundColor: c.accent.success, paddingVertical: 18, borderRadius: 18, alignItems: 'center', ...sh.glow(c.accent.success) },
  sendButtonText: { color: c.text.primary, fontSize: 16, fontFamily: font.extrabold },
  controlBtn: { paddingVertical: 14, borderRadius: 14, alignItems: 'center' },
  controlBtnText: { color: c.text.primary, fontSize: 14, fontFamily: font.bold },
  modalOverlay: { flex: 1, justifyContent: 'center', alignItems: 'center' },
});

export default function FilesScreen() {
  return (
    <AppErrorBoundary fallbackTitle="Files screen crashed">
      <FilesScreenInner />
    </AppErrorBoundary>
  );
}
