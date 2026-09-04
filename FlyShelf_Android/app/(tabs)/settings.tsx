import React, { useState, useCallback, useEffect, useMemo } from 'react';
import AppErrorBoundary from '../../components/AppErrorBoundary';
import * as Haptics from 'expo-haptics';
import { StyleSheet, View, Text, TextInput, TouchableOpacity, KeyboardAvoidingView, Platform, Alert, Switch, NativeModules, ScrollView, ActivityIndicator, Linking, Modal } from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import { Ionicons } from '@expo/vector-icons';
import Animated, { useSharedValue } from 'react-native-reanimated';
import AsyncStorage from '@react-native-async-storage/async-storage';
import * as FileSystem from 'expo-file-system/legacy';
import EncryptedStorage from '../../utils/EncryptedStorage';
import { DOWNLOAD_BASE, SYNC_CACHE_BASE, IMAGE_CACHE_BASE, CONVERTED_BASE } from '../../utils/clipTypes';
import { useSettings, DeviceSyncPrefs } from '../../context/SettingsContext';
import { toast } from '../../context/ToastContext';
import { getDebugLogs, getNetworkLogsText, getNetworkLogCount, clearDebugLogs, clearNetworkLogs, getFormattedReport } from '../../utils/debugLog';
import * as Clipboard from 'expo-clipboard';


import Constants from 'expo-constants';
import { useRouter } from 'expo-router';
import { font, radius, shadows, space } from '../../styles/theme';
import { useAppTheme } from '../../hooks/useAppTheme';

import DeviceHub from '../../components/DeviceHub';
import ScreenHeader from '../../components/ScreenHeader';
import StepSlider from '../../components/StepSlider';


const APP_VERSION = Constants.expoConfig?.version || '1.0.0';
const VERSION_URL = 'https://raw.githubusercontent.com/shdra06/FlyShelf/main/version.json';

// StepSlider extracted to components/StepSlider.tsx

function SettingsScreenInner() {
  const { colors } = useAppTheme();
  const router = useRouter();
  const styles = useMemo(() => createStyles(colors), [colors]);
  const { pcLocalIp, setPcLocalIp, isGlobalSyncEnabled, setGlobalSyncEnabled, deviceName, setDeviceName, isFloatingBallEnabled, setFloatingBallEnabled, floatingBallSize, setFloatingBallSize, floatingBallAutoHide, setFloatingBallAutoHide, pairedDevices, syncPreferences, setSyncPreference, getSyncPrefsForDevice, autoSyncTop5, setAutoSyncTop5, isOfflineOutboxEnabled, setIsOfflineOutboxEnabled, isFcmSilentWakeEnabled, setIsFcmSilentWakeEnabled, defaultHomeCard, setDefaultHomeCard, showBottomHomeSwitcher, setShowBottomHomeSwitcher } = useSettings();
  const [localIpInput, setLocalIpInput] = useState(pcLocalIp);
  const [deviceNameInput, setDeviceNameInput] = useState(deviceName);

  // ═══ Junk Cleaner State ═══
  const [showJunkCleaner, setShowJunkCleaner] = useState(false);
  const [junkAgeFilter, setJunkAgeFilter] = useState<'all' | '24h' | '3d' | '7d' | '30d'>('7d');
  const [cleanDuplicates, setCleanDuplicates] = useState(true);
  const [cleanWhitespace, setCleanWhitespace] = useState(true);
  const [cleanMicroSnips, setCleanMicroSnips] = useState(true);
  const [protectPinned, setProtectPinned] = useState(true);
  const [cleanBrokenFiles, setCleanBrokenFiles] = useState(true);
  const [junkPreviewCount, setJunkPreviewCount] = useState<number | null>(null);
  const [totalClipsCount, setTotalClipsCount] = useState<number>(0);

  const analyzeJunk = useCallback(async () => {
    try {
      const raw = await EncryptedStorage.getItem('@flyshelf_clips') || await AsyncStorage.getItem('@flyshelf_clips');
      if (!raw) {
        setJunkPreviewCount(0);
        setTotalClipsCount(0);
        return;
      }
      const list = JSON.parse(raw);
      if (!Array.isArray(list)) return;
      setTotalClipsCount(list.length);

      const now = Date.now();
      const ageCutoff = junkAgeFilter === '24h' ? now - (24 * 60 * 60 * 1000)
        : junkAgeFilter === '3d' ? now - (3 * 24 * 60 * 60 * 1000)
        : junkAgeFilter === '7d' ? now - (7 * 24 * 60 * 60 * 1000)
        : junkAgeFilter === '30d' ? now - (30 * 24 * 60 * 60 * 1000)
        : 0;

      const seenContent = new Set<string>();
      let junkCount = 0;

      for (const item of list) {
        if (protectPinned && item.IsPinned) continue;

        let isJunk = false;
        if (ageCutoff > 0 && (item.Timestamp || 0) < ageCutoff) isJunk = true;
        const text = (item.Raw || item.Title || '').trim();
        if (cleanWhitespace && !text) isJunk = true;
        if (cleanMicroSnips && text.length > 0 && text.length <= 2) isJunk = true;
        if (cleanDuplicates && text) {
          if (seenContent.has(text)) isJunk = true;
          else seenContent.add(text);
        }
        if (cleanBrokenFiles && item.CachedUri) {
          try {
            const exists = await FileSystem.getInfoAsync(item.CachedUri);
            if (!exists.exists) isJunk = true;
          } catch {
            isJunk = true;
          }
        }

        if (isJunk) junkCount++;
      }

      setJunkPreviewCount(junkCount);
    } catch (e) {
      console.warn('Junk analyze error:', e);
    }
  }, [junkAgeFilter, cleanDuplicates, cleanWhitespace, cleanMicroSnips, cleanBrokenFiles, protectPinned]);

  useEffect(() => {
    if (showJunkCleaner) {
      analyzeJunk();
    }
  }, [showJunkCleaner, analyzeJunk]);

  const executeCleanJunk = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    try {
      const raw = await EncryptedStorage.getItem('@flyshelf_clips') || await AsyncStorage.getItem('@flyshelf_clips');
      if (!raw) {
        toast.info('Clean', 'No clips found to clean');
        return;
      }
      const list = JSON.parse(raw);
      if (!Array.isArray(list)) return;

      const now = Date.now();
      const ageCutoff = junkAgeFilter === '24h' ? now - (24 * 60 * 60 * 1000)
        : junkAgeFilter === '3d' ? now - (3 * 24 * 60 * 60 * 1000)
        : junkAgeFilter === '7d' ? now - (7 * 24 * 60 * 60 * 1000)
        : junkAgeFilter === '30d' ? now - (30 * 24 * 60 * 60 * 1000)
        : 0;

      const seenContent = new Set<string>();
      const kept: any[] = [];
      let removedCount = 0;

      for (const item of list) {
        if (protectPinned && item.IsPinned) {
          kept.push(item);
          continue;
        }

        let isJunk = false;
        if (ageCutoff > 0 && (item.Timestamp || 0) < ageCutoff) isJunk = true;
        const text = (item.Raw || item.Title || '').trim();
        if (cleanWhitespace && !text) isJunk = true;
        if (cleanMicroSnips && text.length > 0 && text.length <= 2) isJunk = true;
        if (cleanDuplicates && text) {
          if (seenContent.has(text)) isJunk = true;
          else seenContent.add(text);
        }
        if (cleanBrokenFiles && item.CachedUri) {
          try {
            const exists = await FileSystem.getInfoAsync(item.CachedUri);
            if (!exists.exists) isJunk = true;
          } catch {
            isJunk = true;
          }
        }

        if (isJunk) {
          removedCount++;
        } else {
          kept.push(item);
        }
      }

      const json = JSON.stringify(kept);
      const diskClipsBackup = `${FileSystem.documentDirectory}flyshelf_clips_backup.json`;
      await Promise.all([
        EncryptedStorage.setItem('@flyshelf_clips', json).catch(() => {}),
        AsyncStorage.setItem('@flyshelf_clips', json).catch(() => {}),
        FileSystem.writeAsStringAsync(diskClipsBackup, json).catch(() => {}),
      ]);

      if (ageCutoff > 0) {
        await AsyncStorage.setItem('localWipeTimestamp', ageCutoff.toString()).catch(() => {});
      }

      setShowJunkCleaner(false);
      toast.success('Clipboard Cleaned', `Removed ${removedCount} junk items. ${kept.length} kept.`);
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    } catch (e: any) {
      Alert.alert('Cleaning Error', e?.message || 'Failed to clean clipboard');
    }
  };

  // Sync local state when context values change (e.g. loaded from storage)
  useEffect(() => { setLocalIpInput(pcLocalIp || ''); }, [pcLocalIp]);
  useEffect(() => { setDeviceNameInput(deviceName || ''); }, [deviceName]);
  const [showDeviceHub, setShowDeviceHub] = useState(false);

  // ═══ Update System State ═══
  const [updateStatus, setUpdateStatus] = useState<'idle' | 'checking' | 'available' | 'error'>('idle');
  const [latestVersion, setLatestVersion] = useState('');
  const [changelog, setChangelog] = useState('');
  const [downloadUrl, setDownloadUrl] = useState('');

  const { AdvanceOverlay } = NativeModules;

  const [, setLogRefreshKey] = useState(0);

  const handleCopyAllLogs = async () => {
    try {
      Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
      const report = await getFormattedReport();
      await Clipboard.setStringAsync(report);
      toast.success('Logs Copied', 'All logs copied to clipboard');
    } catch (e: any) {
      Alert.alert('Error', 'Failed to copy debug report');
    }
  };

  const handleCopyNetworkLogs = async () => {
    try {
      Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
      const netLogs = getNetworkLogsText();
      await Clipboard.setStringAsync(netLogs);
      toast.success('Logs Copied', 'Network logs copied to clipboard');
    } catch (e: any) {
      Alert.alert('Error', 'Failed to copy network logs');
    }
  };

  const handleClearLogs = () => {
    try {
      Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
      clearDebugLogs();
      clearNetworkLogs();
      setLogRefreshKey(prev => prev + 1);
      toast.success('Logs Cleared', 'Debug and network logs cleared');
    } catch (e: any) {
      Alert.alert('Error', 'Failed to clear debug logs');
    }
  };

  const handleSave = async () => {
    try {
      Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
      // Validate IP format if provided
      if (localIpInput.trim()) {
        const ipPortMatch = localIpInput.trim().match(/^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(:\d+)?$/);
        if (!ipPortMatch || [ipPortMatch[1], ipPortMatch[2], ipPortMatch[3], ipPortMatch[4]].some(octet => Number(octet) > 255)) {
          Alert.alert('Invalid IP', 'Enter a valid IP (e.g., 192.168.1.5:8999). Each octet must be 0-255.');
          return;
        }
      }

      await setPcLocalIp(localIpInput);
      await setDeviceName(deviceNameInput);

      if (Platform.OS === 'android' && AdvanceOverlay) {
        if (isFloatingBallEnabled) {
          try {
            const hasPerm = await AdvanceOverlay.checkOverlayPermission();
            if (!hasPerm) {
              try { await AdvanceOverlay.requestOverlayPermission(); } catch (e) { console.warn('Overlay module error:', e); }
              Alert.alert('Permission Required', 'Please enable Draw Over Other Apps in settings, switch back, and press save again.');
              return;
            } else {
              try { AdvanceOverlay.startOverlay(); } catch (e) { console.warn('Overlay module error:', e); }
              try { AdvanceOverlay.setBallVisible?.(true); } catch (e) {}
              try { AdvanceOverlay.setOverlayConfig(floatingBallSize, floatingBallAutoHide); } catch (e) { console.warn('Overlay module error:', e); }
            }
          } catch (e) { console.warn('Overlay module error:', e); }
        } else {
          // Hide floating ball UI, but keep foreground service running for background sync if sync is enabled
          try {
            AdvanceOverlay.setBallVisible?.(false);
            if (!isGlobalSyncEnabled) {
              AdvanceOverlay.stopOverlay();
            } else {
              AdvanceOverlay.startOverlay();
            }
          } catch (e) { console.warn('Overlay module error:', e); }
        }
      }

      Alert.alert('Saved', 'Configuration preserved.');
    } catch (e: any) {
      Alert.alert('Error', e?.message || 'Failed to save settings.');
    }
  };

  // ═══ Update Functions ═══
  const checkForUpdate = useCallback(async () => {
    try {
      setUpdateStatus('checking');
      const _ctrl = new AbortController();
      const _timeout = setTimeout(() => _ctrl.abort(), 10000);
      let res;
      try {
        res = await fetch(`${VERSION_URL}?t=${Date.now()}`, { signal: _ctrl.signal });
      } finally {
        clearTimeout(_timeout);
      }
      if (!res.ok) throw new Error('HTTP ' + res.status);
      const data = await res.json();
      const latest = data.android_version || '1.0.0';
      const dl = data.android_download || '';
      const log = data.changelog || data.android_changelog || 'Bug fixes and improvements';

      setLatestVersion(latest);
      setChangelog(log);
      setDownloadUrl(dl);

      // Simple semver compare
      const currentParts = APP_VERSION.split('.').map(Number);
      const latestParts = latest.split('.').map(Number);
      let isNewer = false;
      for (let i = 0; i < 3; i++) {
        if ((latestParts[i] || 0) > (currentParts[i] || 0)) { isNewer = true; break; }
        if ((latestParts[i] || 0) < (currentParts[i] || 0)) break;
      }

      if (isNewer && dl) {
        setUpdateStatus('available');
        Alert.alert(
          '🎉 Update Available',
          `A new version (v${latest}) of FlyShelf is available.\n\nChangelog:\n${log}\n\nWould you like to open your web browser to download and install this update?`,
          [
            { text: 'Cancel', style: 'cancel' },
            {
              text: 'Update Now',
              style: 'default',
              onPress: async () => {
                try {
                  await Linking.openURL(dl);
                } catch {
                  Alert.alert('Error', 'Could not open update link.');
                }
              }
            }
          ]
        );
      } else {
        setUpdateStatus('idle');
        Alert.alert('✅ Up to Date', `You're on the latest version (v${APP_VERSION}).`);
      }
    } catch (e) {
      setUpdateStatus('error');
      Alert.alert('Error', 'Could not check for updates. Check your internet connection.');
    }
  }, []);

  const getUpdateButtonContent = () => {
    switch (updateStatus) {
      case 'checking':
        return (
          <View style={{ flexDirection: 'row', alignItems: 'center', gap: 8 }}>
            <ActivityIndicator size="small" color={colors.text.primary} />
            <Text style={{ color: colors.text.primary, fontFamily: font.bold, fontSize: 13 }}>Checking...</Text>
          </View>
        );
      case 'available':
        return <Text style={{ color: colors.text.primary, fontFamily: font.bold, fontSize: 13 }}>Download v{latestVersion}</Text>;
      case 'error':
        return <Text style={{ color: colors.text.primary, fontFamily: font.bold, fontSize: 13 }}>Retry Check</Text>;
      default:
        return <Text style={{ color: colors.text.primary, fontFamily: font.bold, fontSize: 13 }}>Check Updates</Text>;
    }
  };

  const handleUpdatePress = () => {
    switch (updateStatus) {
      case 'idle':
      case 'error':
        checkForUpdate();
        break;
      case 'available':
        if (downloadUrl) {
          Linking.openURL(downloadUrl).catch(() => Alert.alert('Error', 'Could not open update link.'));
        }
        break;
    }
  };

  const getUpdateButtonColor = () => {
    switch (updateStatus) {
      case 'available': return '#F59E0B';
      case 'error': return colors.accent.error;
      default: return colors.accent.success;
    }
  };

  const scrollY = useSharedValue(0);
  const scrollHandler = (e: any) => {
    const offsetY = e?.nativeEvent?.contentOffset?.y;
    if (typeof offsetY === 'number') {
      scrollY.value = offsetY;
    }
  };

  return (
    <LinearGradient colors={[colors.bg.base, colors.bg.baseEnd]} style={{ flex: 1 }}>
    <View style={[styles.container, { backgroundColor: 'transparent' }]}>
      <ScreenHeader title="Settings" subtitle="Configuration" scrollY={scrollY} rightActions={
        <TouchableOpacity onPress={() => router.navigate('/')} hitSlop={12} style={{ padding: 6, borderRadius: 20, backgroundColor: colors.bg.elevated }}>
          <Ionicons name="close" size={20} color={colors.text.secondary} />
        </TouchableOpacity>
      } />
      <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : 'height'} style={{flex: 1}}>
        <Animated.ScrollView contentContainerStyle={styles.scrollContent} keyboardShouldPersistTaps="handled" showsVerticalScrollIndicator={false} onScroll={scrollHandler} scrollEventThrottle={16}>

          {/* Networking Card */}
          <View style={[styles.card, { marginTop: 16 }]}>
            <Text style={styles.sectionHeader}>Networking</Text>
            
            <View style={styles.inputContainer}>
              <View style={styles.inputHeaderRow}>
                <Ionicons name="wifi" size={20} color={colors.accent.primary} />
                <Text style={styles.inputLabel}>Computer IP</Text>
              </View>
              <TextInput
                style={styles.input}
                value={localIpInput}
                onChangeText={setLocalIpInput}
                placeholder="e.g. 192.168.1.5:8999"
                placeholderTextColor={colors.text.tertiary}
                keyboardType="numbers-and-punctuation"
                accessibilityLabel="PC API address"
                accessibilityRole="text"
              />
              <Text style={styles.helperText}>Fallback IP for direct LAN transfers when your PC isn't auto-detected. If your PC shows up in Active Devices, this can be left blank. Format: 192.168.x.x:8999</Text>
            </View>

            <View style={[styles.inputContainer, { marginTop: 20 }]}>
              <View style={styles.inputHeaderRow}>
                <Ionicons name="phone-portrait-outline" size={20} color={colors.accent.primary} />
                <Text style={styles.inputLabel}>Your Device Name</Text>
              </View>
              <TextInput
                style={styles.input}
                value={deviceNameInput}
                onChangeText={setDeviceNameInput}
                placeholder="e.g. John's Mobile Profile"
                placeholderTextColor={colors.text.tertiary}
                accessibilityLabel="Device profile name"
                accessibilityRole="text"
              />
              <Text style={styles.helperText}>This name identifies you on the clipboard feed.</Text>
            </View>



            <View style={[styles.inputContainer, { marginTop: 20 }]}>
              <View style={{flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center'}}>
                  <View style={styles.inputHeaderRow}>
                    <Ionicons name="cloud-outline" size={20} color={colors.accent.primary} />
                    <Text style={styles.inputLabel}>Cloud Discovery</Text>
                  </View>
                  <Switch 
                    value={isGlobalSyncEnabled} 
                    onValueChange={(val) => { Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); setGlobalSyncEnabled(val); }} 
                    trackColor={{ false: colors.text.disabled, true: "rgba(99,132,255,0.4)" }} 
                    thumbColor="#FFF"
                    accessibilityLabel={isGlobalSyncEnabled ? 'Cloud discovery enabled' : 'Cloud discovery disabled'}
                    accessibilityRole="switch"
                  />
              </View>
              <Text style={styles.helperText}>Allows paired devices to find each other over the internet. If disabled, sync only works on the same local network.</Text>
            </View>

            <View style={[styles.inputContainer, { marginTop: 20 }]}>
              <View style={{flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center'}}>
                  <View style={styles.inputHeaderRow}>
                    <Ionicons name="sync-outline" size={20} color={colors.accent.primary} />
                    <Text style={styles.inputLabel}>Auto-Sync Recent Copies</Text>
                  </View>
                  <Switch 
                    value={autoSyncTop5} 
                    onValueChange={(val) => { 
                      try { Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light).catch(() => {}); } catch {}
                      setAutoSyncTop5(val); 
                    }} 
                    trackColor={{ false: colors.text.disabled, true: "rgba(99,132,255,0.4)" }} 
                    thumbColor="#FFF"
                    accessibilityLabel={autoSyncTop5 ? 'Auto-sync top 5 items enabled' : 'Single latest item sync mode enabled'}
                    accessibilityRole="switch"
                  />
              </View>
              <Text style={styles.helperText}>
                {autoSyncTop5
                  ? 'Automatically fetches and syncs the top 5 recent clipboard items in sequence when connecting.'
                  : 'Single-item mode: only syncs the latest incoming item upon connection.'}
              </Text>
            </View>

            <View style={[styles.inputContainer, { marginTop: 20 }]}>
              <View style={{flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center'}}>
                  <View style={styles.inputHeaderRow}>
                    <Ionicons name="cloud-offline-outline" size={20} color={colors.accent.primary} />
                    <Text style={styles.inputLabel}>Offline Outbox Queue</Text>
                    {/* PRO badge — uncomment when Pro licensing is enforced:
                    <View style={{backgroundColor: 'rgba(255,165,0,0.15)', borderRadius: 4, paddingHorizontal: 6, paddingVertical: 2, marginLeft: 6}}>
                      <Text style={{color: '#FFA500', fontSize: 10, fontWeight: '700'}}>PRO</Text>
                    </View>
                    */}
                  </View>
                  <Switch 
                    value={isOfflineOutboxEnabled} 
                    onValueChange={(val) => { 
                      try { Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light).catch(() => {}); } catch {}
                      setIsOfflineOutboxEnabled(val); 
                    }} 
                    trackColor={{ false: colors.text.disabled, true: "rgba(99,132,255,0.4)" }} 
                    thumbColor="#FFF"
                    accessibilityLabel={isOfflineOutboxEnabled ? 'Offline outbox queue enabled' : 'Offline outbox queue disabled'}
                    accessibilityRole="switch"
                  />
              </View>
              <Text style={styles.helperText}>
                {isOfflineOutboxEnabled
                  ? 'Items copied while offline will be queued and automatically synced when a direct connection is re-established.'
                  : 'Disabled: only new items arriving after a live connection is established will sync. No offline queuing.'}
              </Text>
            </View>

            <View style={[styles.inputContainer, { marginTop: 20 }]}>
              <View style={{flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center'}}>
                  <View style={styles.inputHeaderRow}>
                    <Ionicons name="flash-outline" size={20} color={colors.accent.primary} />
                    <Text style={styles.inputLabel}>FCM High-Priority Silent Wake</Text>
                  </View>
                  <Switch 
                    value={isFcmSilentWakeEnabled} 
                    onValueChange={(val) => { 
                      try { Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light).catch(() => {}); } catch {}
                      setIsFcmSilentWakeEnabled(val); 
                    }} 
                    trackColor={{ false: colors.text.disabled, true: "rgba(99,132,255,0.4)" }} 
                    thumbColor="#FFF"
                    accessibilityLabel={isFcmSilentWakeEnabled ? 'FCM silent wake enabled' : 'FCM silent wake disabled'}
                    accessibilityRole="switch"
                  />
              </View>
              <Text style={styles.helperText}>
                Allows your PC to silently wake your phone when you copy an item or transfer a file while the phone is asleep. The Floating Ball receives the copied content instantly without opening the app.
              </Text>
            </View>
          </View>

          {/* Devices Card — launches DeviceHub */}
          <View style={[styles.card, { marginTop: 16 }]}>
            <Text style={styles.sectionHeader}>Devices</Text>

            <TouchableOpacity
              onPress={() => { Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); setShowDeviceHub(true); }}
              activeOpacity={0.7}
              style={{
                backgroundColor: colors.bg.input,
                borderRadius: radius.lg,
                padding: space.lg,
                borderWidth: 1,
                borderColor: colors.border.subtle,
              }}
              accessibilityLabel={pairedDevices.length > 0 ? `${pairedDevices.length} devices paired, tap to manage` : 'No devices paired, tap to pair'}
              accessibilityRole="button"
            >
              <View style={{ flexDirection: 'row', alignItems: 'center' }}>
                <View style={{
                  width: 44, height: 44, borderRadius: 12,
                  backgroundColor: colors.accent.primaryDim,
                  justifyContent: 'center', alignItems: 'center', marginRight: space.md,
                }}>
                  <Ionicons name="laptop-outline" size={22} color={colors.accent.primary} />
                </View>
                <View style={{ flex: 1 }}>
                  <Text style={{ color: colors.text.primary, fontSize: 15, fontFamily: font.semibold }}>
                    {pairedDevices.length > 0 ? `${pairedDevices.length} Device${pairedDevices.length > 1 ? 's' : ''} Paired` : 'No Devices Paired'}
                  </Text>
                  <Text style={{ color: colors.text.secondary, fontSize: 12, fontFamily: font.regular, marginTop: 2 }}>
                    {pairedDevices.length > 0 
                      ? `Tap to manage • ${pairedDevices.filter(d => d.deviceType === 'PC').length} PC, ${pairedDevices.filter(d => d.deviceType === 'Mobile').length} Mobile`
                      : 'Tap to pair your first device'
                    }
                  </Text>
                </View>
                <Ionicons name="chevron-forward" size={16} color={colors.text.tertiary} />
              </View>

              {/* Mini device preview — show small avatars of paired devices */}
              {pairedDevices.length > 0 && (
                <View style={{ flexDirection: 'row', marginTop: space.md, gap: space.sm }}>
                  {pairedDevices.slice(0, 5).map((device) => (
                    <View key={device.deviceId} style={{
                      width: 32, height: 32, borderRadius: 10,
                      backgroundColor: device.deviceType === 'PC' ? colors.accent.infoDim 
                        : device.deviceType === 'Mobile' ? colors.accent.successDim 
                        : colors.accent.warningDim,
                      justifyContent: 'center', alignItems: 'center',
                      borderWidth: 1, borderColor: colors.border.subtle,
                    }}>
                      <Ionicons 
                        name={device.deviceType === 'PC' ? 'laptop-outline' : device.deviceType === 'Mobile' ? 'phone-portrait-outline' : 'globe-outline'}
                        size={16}
                        color={device.deviceType === 'PC' ? colors.accent.info 
                          : device.deviceType === 'Mobile' ? colors.accent.success 
                          : colors.accent.warning}
                      />
                    </View>
                  ))}
                </View>
              )}
            </TouchableOpacity>
          </View>

          {/* DeviceHub Modal */}
          <DeviceHub visible={showDeviceHub} onClose={() => setShowDeviceHub(false)} />

          {/* ═══════════════════════════════════════════ */}
          {/* Sync Preferences Card — Per-Device Toggles */}
          {/* ═══════════════════════════════════════════ */}
          {pairedDevices.length > 0 && (
            <View style={[styles.card, { marginTop: 16 }]}>
              <Text style={styles.sectionHeader}>Sync Preferences</Text>
              <Text style={styles.helperText}>Control what syncs with each paired device. Disabled categories will not send or receive data.</Text>

              {pairedDevices.map((device) => {
                const prefs = getSyncPrefsForDevice(device.deviceId);
                const SYNC_CATEGORIES: { key: keyof DeviceSyncPrefs; label: string; icon: string; color: string }[] = [
                  { key: 'clipboard', label: 'Clipboard', icon: 'clipboard-outline', color: colors.accent.primary },
                  { key: 'images', label: 'Images', icon: 'image-outline', color: '#F472B6' },
                  { key: 'files', label: 'Files', icon: 'document-outline', color: '#F59E0B' },
                  { key: 'notes', label: 'Notes', icon: 'reader-outline', color: '#34D399' },
                  { key: 'todos', label: 'To-Do', icon: 'checkbox-outline', color: '#8B5CF6' },
                ];
                const enabledCount = SYNC_CATEGORIES.filter(c => prefs[c.key]).length;

                return (
                  <View key={device.deviceId} style={{
                    backgroundColor: colors.bg.input,
                    borderRadius: radius.lg,
                    padding: space.md,
                    marginTop: space.md,
                    borderWidth: 1,
                    borderColor: colors.border.subtle,
                  }}>
                    {/* Device header */}
                    <View style={{ flexDirection: 'row', alignItems: 'center', marginBottom: space.sm }}>
                      <View style={{
                        width: 34, height: 34, borderRadius: 10,
                        backgroundColor: device.deviceType === 'PC' ? colors.accent.infoDim : colors.accent.successDim,
                        justifyContent: 'center', alignItems: 'center', marginRight: space.sm,
                      }}>
                        <Ionicons
                          name={device.deviceType === 'PC' ? 'laptop-outline' : 'phone-portrait-outline'}
                          size={18}
                          color={device.deviceType === 'PC' ? colors.accent.info : colors.accent.success}
                        />
                      </View>
                      <View style={{ flex: 1 }}>
                        <Text style={{ color: colors.text.primary, fontSize: 14, fontFamily: font.semibold }}>
                          {device.deviceName || device.deviceId.slice(0, 8)}
                        </Text>
                        <Text style={{ color: colors.text.tertiary, fontSize: 11, fontFamily: font.regular }}>
                          {enabledCount}/{SYNC_CATEGORIES.length} categories enabled
                        </Text>
                      </View>
                    </View>

                    {/* Category toggles */}
                    {SYNC_CATEGORIES.map((cat) => (
                      <View key={cat.key} style={{
                        flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between',
                        paddingVertical: 8, paddingHorizontal: 4,
                        borderTopWidth: 1, borderTopColor: colors.border.subtle,
                      }}>
                        <View style={{ flexDirection: 'row', alignItems: 'center' }}>
                          <Ionicons name={cat.icon as any} size={18} color={prefs[cat.key] ? cat.color : colors.text.disabled} style={{ marginRight: 10 }} />
                          <Text style={{
                            color: prefs[cat.key] ? colors.text.primary : colors.text.disabled,
                            fontSize: 13, fontFamily: font.medium,
                          }}>{cat.label}</Text>
                        </View>
                        <Switch
                          value={prefs[cat.key]}
                          onValueChange={(val) => {
                            Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
                            setSyncPreference(device.deviceId, cat.key, val);
                          }}
                          trackColor={{ false: colors.text.disabled, true: `${cat.color}66` }}
                          thumbColor="#FFF"
                        />
                      </View>
                    ))}
                  </View>
                );
              })}
            </View>
          )}

          {/* Floating Clipboard Card */}
          <View style={[styles.card, { marginTop: 16 }]}>
            <Text style={styles.sectionHeader}>Floating Clipboard</Text>

            <View style={styles.inputContainer}>
              <View style={{flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center'}}>
                  <View style={styles.inputHeaderRow}>
                    <Ionicons name="eye-outline" size={20} color={colors.type.image} />
                    <Text style={styles.inputLabel}>Enable Floating Ball</Text>
                  </View>
                  <Switch 
                    value={isFloatingBallEnabled} 
                    onValueChange={(val) => { Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); setFloatingBallEnabled(val); }} 
                    trackColor={{ false: colors.text.disabled, true: "rgba(167,139,250,0.4)" }} 
                    thumbColor="#FFF"
                    accessibilityLabel={isFloatingBallEnabled ? 'Floating ball enabled' : 'Floating ball disabled'}
                    accessibilityRole="switch"
                  />
              </View>
              <Text style={styles.helperText}>Enable the persistent floating ball on your screen for instant overlay clipboard access anywhere.</Text>
            </View>

            <View style={[styles.inputContainer, { marginTop: 16 }]}>
              <View style={styles.inputHeaderRow}>
                <Ionicons name="resize-outline" size={20} color={colors.accent.success} />
                <Text style={styles.inputLabel}>Ball Size: {floatingBallSize}dp</Text>
              </View>
              <StepSlider
                value={floatingBallSize}
                min={32}
                max={72}
                step={4}
                onValueChange={(val) => setFloatingBallSize(val)}
                trackColor="#10B981"
                label="size"
              />
              <Text style={styles.helperText}>Controls how large the floating ball appears on screen. Default: 48dp.</Text>
            </View>

            <View style={[styles.inputContainer, { marginTop: 16 }]}>
              <View style={styles.inputHeaderRow}>
                <Ionicons name="timer-outline" size={20} color={colors.accent.warning} />
                <Text style={styles.inputLabel}>Auto-Hide Delay: {(floatingBallAutoHide / 1000).toFixed(1)}s</Text>
              </View>
              <StepSlider
                value={floatingBallAutoHide}
                min={1000}
                max={10000}
                step={500}
                onValueChange={(val) => setFloatingBallAutoHide(val)}
                trackColor="#F59E0B"
                label="delay"
              />
              <Text style={styles.helperText}>Time before the ball auto-hides to the edge. Default: 3 seconds.</Text>
            </View>

            <View style={[styles.inputContainer, { marginTop: 16 }]}>
              <Text style={[styles.helperText, { color: colors.accent.primary, fontWeight: '500' }]}>
                • Tap a clip item to copy it instantly{'\n'}
                • Long-press to drag & drop into any text field{'\n'}
                • Tap the floating ball to toggle the clipboard panel{'\n'}
                • The ball fades to the edge when not in use
              </Text>
            </View>
          </View>

          {/* App Customization & Navigation Card */}
          <View style={[styles.card, { marginTop: 16 }]}>
            <Text style={styles.sectionHeader}>Customization & Navigation</Text>

            <View style={styles.inputContainer}>
              <View style={styles.inputHeaderRow}>
                <Ionicons name="apps-outline" size={20} color={colors.accent.primary} />
                <Text style={styles.inputLabel}>Default Home Launch</Text>
              </View>
              <Text style={styles.helperText}>Select what screen opens when FlyShelf starts:</Text>
              <View style={{ flexDirection: 'row', flexWrap: 'wrap', gap: 6, marginTop: 8 }}>
                {[
                  { id: 'home', label: '🏠 Dashboard' },
                  { id: 'clipboard', label: '📋 Clipboard' },
                  { id: 'archive', label: '📁 Files' },
                  { id: 'vault', label: '📦 Storage' },
                  { id: 'notes', label: '📝 Notes' },
                  { id: 'todo', label: '✏️ Tasks' },
                ].map(item => {
                  const isSelected = defaultHomeCard === item.id;
                  return (
                    <TouchableOpacity
                      key={item.id}
                      onPress={() => {
                        Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
                        setDefaultHomeCard(item.id);
                        toast.success('Saved', `Default screen set to ${item.label}`);
                      }}
                      style={{
                        paddingHorizontal: 12,
                        paddingVertical: 7,
                        borderRadius: 20,
                        backgroundColor: isSelected ? colors.accent.primary : colors.bg.input,
                        borderWidth: 1,
                        borderColor: isSelected ? colors.accent.primary : colors.border.subtle,
                      }}
                    >
                      <Text style={{ fontSize: 12, fontFamily: font.semibold, color: isSelected ? '#FFF' : colors.text.secondary }}>
                        {item.label}
                      </Text>
                    </TouchableOpacity>
                  );
                })}
              </View>
            </View>

            <View style={[styles.inputContainer, { marginTop: 16 }]}>
              <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center' }}>
                <View style={{ flex: 1, paddingRight: 10 }}>
                  <View style={styles.inputHeaderRow}>
                    <Ionicons name="navigate-outline" size={20} color={colors.accent.success} />
                    <Text style={styles.inputLabel}>Lower Home Switcher</Text>
                  </View>
                  <Text style={[styles.helperText, { marginTop: 2 }]}>Floating pill in lower portion to jump back to Home anytime</Text>
                </View>
                <Switch
                  value={showBottomHomeSwitcher}
                  onValueChange={(val) => {
                    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
                    setShowBottomHomeSwitcher(val);
                  }}
                  thumbColor="#FFF"
                  trackColor={{ false: colors.text.disabled, true: colors.accent.success }}
                />
              </View>
            </View>
          </View>

          {/* App Info & Updates Card */}
          <View style={[styles.card, { marginTop: 16 }]}>
            <Text style={styles.sectionHeader}>App Info & Updates</Text>

            <View style={styles.inputContainer}>
              <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center' }}>
                <View style={{ flex: 1 }}>
                  <Text style={styles.inputLabel}>FlyShelf Mobile</Text>
                  <Text style={[styles.helperText, { marginTop: 2 }]}>
                    Installed: <Text style={{ color: '#8B5CF6', fontWeight: '700' }}>v{APP_VERSION}</Text>
                    {latestVersion && updateStatus === 'available' ? (
                      <Text style={{ color: '#F59E0B' }}>  →  v{latestVersion}</Text>
                    ) : null}
                  </Text>
                </View>
                <TouchableOpacity
                  style={{
                    backgroundColor: getUpdateButtonColor(),
                    paddingHorizontal: 16,
                    paddingVertical: 10,
                    borderRadius: radius.md,
                    minWidth: 130,
                    alignItems: 'center',
                  }}
                  onPress={() => { Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); handleUpdatePress(); }}
                  disabled={updateStatus === 'checking'}
                  accessibilityLabel={updateStatus === 'available' ? `Download version ${latestVersion}` : updateStatus === 'checking' ? 'Checking for updates' : 'Check for updates'}
                  accessibilityRole="button"
                >
                  {getUpdateButtonContent()}
                </TouchableOpacity>
              </View>

              {/* Update Available Info */}
              {updateStatus === 'available' && (
                <View style={{
                  marginTop: 12,
                  backgroundColor: colors.bg.card,
                  borderRadius: 12,
                  padding: 14,
                  borderWidth: 1,
                  borderColor: colors.accent.warning + '33',
                }}>
                  <Text style={{ color: colors.accent.warning, fontWeight: '700', fontSize: 14, marginBottom: 4 }}>
                    🎉 Update v{latestVersion} Available
                  </Text>
                  <Text style={{ color: colors.text.secondary, fontSize: 12, lineHeight: 18 }}>
                    {changelog}
                  </Text>
                </View>
              )}

              <Text style={[styles.helperText, { marginTop: 12 }]}>
                Checks for the latest updates of FlyShelf and opens the direct download link in your device's web browser, allowing Android to securely install the update without needing sensitive package installer permissions.
              </Text>
            </View>
          </View>



          {/* About Section */}
          <View style={[styles.card, { marginTop: 16 }]}>
            <Text style={styles.sectionHeader}>About</Text>

            <View style={styles.inputContainer}>
              <View style={styles.inputHeaderRow}>
                <Ionicons name="information-circle-outline" size={20} color={colors.accent.primary} />
                <Text style={styles.inputLabel}>FlyShelf Mobile</Text>
              </View>
              <Text style={[styles.helperText, { marginTop: 0 }]}>
                Version: <Text style={{ color: colors.type.image, fontWeight: '700' }}>v{APP_VERSION}</Text>
              </Text>
            </View>

            <TouchableOpacity
              style={{
                backgroundColor: colors.bg.input,
                borderRadius: radius.md,
                padding: space.lg,
                marginTop: space.md,
                flexDirection: 'row',
                alignItems: 'center',
                gap: space.sm,
                borderWidth: 1,
                borderColor: colors.border.subtle,
              }}
              onPress={() => { Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); Linking.openURL('https://flyshelf.app/privacy.html').catch(() => Alert.alert('Error', 'Could not open privacy policy.')); }}
              accessibilityLabel="Open privacy policy"
              accessibilityRole="link"
            >
              <Ionicons name="shield-checkmark-outline" size={18} color={colors.accent.info} />
              <Text style={{ color: colors.accent.info, fontSize: 14, fontFamily: font.semibold, flex: 1 }}>Privacy Policy</Text>
              <Ionicons name="open-outline" size={14} color={colors.text.tertiary} />
            </TouchableOpacity>

            {/* ═══ Advanced Deletion / Mass Delete Section ═══ */}
            <View style={{ marginTop: space.lg, paddingTop: space.md, borderTopWidth: 1, borderTopColor: colors.border.subtle }}>
              <Text style={{ color: colors.text.primary, fontSize: 14, fontFamily: font.semibold, marginBottom: space.sm }}>
                Advanced Clean Up & Mass Deletion
              </Text>
              <Text style={[styles.helperText, { marginBottom: space.md }]}>
                Choose specific data types or timeframes to mass delete from local storage.
              </Text>

              {/* Action 0: Advanced Junk Cleaner */}
              <TouchableOpacity
                style={{
                  backgroundColor: colors.accent.primary + '18',
                  borderRadius: radius.md,
                  padding: space.md,
                  flexDirection: 'row',
                  alignItems: 'center',
                  gap: space.sm,
                  borderWidth: 1,
                  borderColor: colors.accent.primary + '40',
                  marginBottom: space.sm,
                }}
                onPress={() => {
                  Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
                  setShowJunkCleaner(true);
                }}
              >
                <Ionicons name="sparkles" size={20} color={colors.accent.primary} />
                <View style={{ flex: 1 }}>
                  <Text style={{ color: colors.accent.primary, fontSize: 14, fontFamily: font.bold }}>Advanced Clipboard Junk Cleaner</Text>
                  <Text style={{ color: colors.text.secondary, fontSize: 11 }}>Smart scan for duplicates, empty items & aged history</Text>
                </View>
                <Ionicons name="chevron-forward" size={16} color={colors.accent.primary} />
              </TouchableOpacity>

              {/* Action 1: Mass Delete All Clips */}
              <TouchableOpacity
                style={{
                  backgroundColor: colors.accent.errorDim,
                  borderRadius: radius.md,
                  padding: space.md,
                  flexDirection: 'row',
                  alignItems: 'center',
                  gap: space.sm,
                  borderWidth: 1,
                  borderColor: colors.border.subtle,
                  marginBottom: space.sm,
                }}
                onPress={() => {
                  Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
                  Alert.alert(
                    'Delete All Saved Clips',
                    'Are you sure you want to delete all saved clipboard items? This cannot be undone.',
                    [
                      { text: 'Cancel', style: 'cancel' },
                      {
                        text: 'Delete All',
                        style: 'destructive',
                        onPress: async () => {
                          try {
                            await EncryptedStorage.removeItem('@flyshelf_clips');
                            await AsyncStorage.removeItem('@flyshelf_clips');
                            await AsyncStorage.setItem('localWipeTimestamp', Date.now().toString());
                            toast.success('Clips Deleted', 'All saved clips removed from storage.');
                            Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
                          } catch (e: any) {
                            Alert.alert('Error', e?.message || 'Failed to clear clips.');
                          }
                        },
                      },
                    ]
                  );
                }}
              >
                <Ionicons name="trash-outline" size={18} color={colors.accent.error} />
                <View style={{ flex: 1 }}>
                  <Text style={{ color: colors.accent.error, fontSize: 14, fontFamily: font.semibold }}>Delete All Synced Clips</Text>
                  <Text style={{ color: colors.text.tertiary, fontSize: 11 }}>Wipes all clipboard history from memory & disk</Text>
                </View>
              </TouchableOpacity>

              {/* Action 2: Clear Media & File Cache */}
              <TouchableOpacity
                style={{
                  backgroundColor: colors.bg.input,
                  borderRadius: radius.md,
                  padding: space.md,
                  flexDirection: 'row',
                  alignItems: 'center',
                  gap: space.sm,
                  borderWidth: 1,
                  borderColor: colors.border.subtle,
                  marginBottom: space.sm,
                }}
                onPress={() => {
                  Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
                  Alert.alert(
                    'Clear Media & File Cache',
                    'Delete downloaded images, documents, and converted PDFs from device storage to free up space. Text items will be preserved.',
                    [
                      { text: 'Cancel', style: 'cancel' },
                      {
                        text: 'Clear Media Files',
                        style: 'destructive',
                        onPress: async () => {
                          try {
                            const dirs = [DOWNLOAD_BASE, SYNC_CACHE_BASE, IMAGE_CACHE_BASE, CONVERTED_BASE];
                            for (const dir of dirs) {
                              try {
                                const info = await FileSystem.getInfoAsync(dir);
                                if (info.exists) await FileSystem.deleteAsync(dir, { idempotent: true });
                                await FileSystem.makeDirectoryAsync(dir, { intermediates: true });
                              } catch {}
                            }
                            Alert.alert('Success', 'Media and file cache cleared successfully.');
                          } catch (e: any) {
                            Alert.alert('Error', e?.message || 'Failed to clear media cache.');
                          }
                        },
                      },
                    ]
                  );
                }}
              >
                <Ionicons name="images-outline" size={18} color={colors.accent.primary} />
                <View style={{ flex: 1 }}>
                  <Text style={{ color: colors.text.primary, fontSize: 14, fontFamily: font.semibold }}>Clear Media & File Cache</Text>
                  <Text style={{ color: colors.text.tertiary, fontSize: 11 }}>Frees storage while keeping your text history</Text>
                </View>
              </TouchableOpacity>

              {/* Action 3: Purge Older Than 7 Days */}
              <TouchableOpacity
                style={{
                  backgroundColor: colors.bg.input,
                  borderRadius: radius.md,
                  padding: space.md,
                  flexDirection: 'row',
                  alignItems: 'center',
                  gap: space.sm,
                  borderWidth: 1,
                  borderColor: colors.border.subtle,
                }}
                onPress={() => {
                  Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
                  Alert.alert(
                    'Delete History Older than 7 Days',
                    'Remove items that are older than 7 days from local history.',
                    [
                      { text: 'Cancel', style: 'cancel' },
                      {
                        text: 'Purge Old Items',
                        onPress: async () => {
                          try {
                            const raw = await EncryptedStorage.getItem('@flyshelf_clips') || await AsyncStorage.getItem('@flyshelf_clips');
                            if (raw) {
                              const list = JSON.parse(raw);
                              if (!Array.isArray(list)) return;
                              const cutoff = Date.now() - (7 * 24 * 60 * 60 * 1000);
                              const filtered = list.filter((item: any) => (item.Timestamp || 0) > cutoff || item.IsPinned);
                              const json = JSON.stringify(filtered);
                              await EncryptedStorage.setItem('@flyshelf_clips', json);
                              await AsyncStorage.setItem('@flyshelf_clips', json);
                              await AsyncStorage.setItem('localWipeTimestamp', cutoff.toString());
                              toast.success('Cleaned', `Purged old items. ${filtered.length} recent items kept.`);
                              Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
                            } else {
                              Alert.alert('Info', 'No items found to clean.');
                            }
                          } catch (e: any) {
                            Alert.alert('Error', e?.message || 'Failed to clean old items.');
                          }
                        },
                      },
                    ]
                  );
                }}
              >
                <Ionicons name="time-outline" size={18} color={colors.accent.primary} />
                <View style={{ flex: 1 }}>
                  <Text style={{ color: colors.text.primary, fontSize: 14, fontFamily: font.semibold }}>Delete Older Than 7 Days</Text>
                  <Text style={{ color: colors.text.tertiary, fontSize: 11 }}>Keep only this week's clips and pinned items</Text>
                </View>
              </TouchableOpacity>
            </View>
          </View>

          {/* Diagnostics Card */}
          <View style={[styles.card, { marginTop: 16 }]}>
            <Text style={styles.sectionHeader}>🔧 Diagnostics & Debug</Text>

            <View style={styles.inputContainer}>
              <Text style={[styles.helperText, { marginTop: 0, marginBottom: space.md }]}>
                {getNetworkLogCount()} network events logged
              </Text>
            </View>

            <TouchableOpacity
              style={{
                backgroundColor: colors.bg.input,
                borderRadius: radius.md,
                padding: space.md,
                flexDirection: 'row',
                alignItems: 'center',
                gap: space.sm,
                borderWidth: 1,
                borderColor: colors.border.subtle,
                marginBottom: space.sm,
              }}
              onPress={handleCopyAllLogs}
              accessibilityLabel="📋 Copy All Logs"
              accessibilityRole="button"
            >
              <Text style={{ color: colors.text.primary, fontSize: 14, fontFamily: font.semibold, flex: 1 }}>
                📋 Copy All Logs
              </Text>
              <Ionicons name="copy-outline" size={16} color={colors.accent.primary} />
            </TouchableOpacity>

            <TouchableOpacity
              style={{
                backgroundColor: colors.bg.input,
                borderRadius: radius.md,
                padding: space.md,
                flexDirection: 'row',
                alignItems: 'center',
                gap: space.sm,
                borderWidth: 1,
                borderColor: colors.border.subtle,
                marginBottom: space.sm,
              }}
              onPress={handleCopyNetworkLogs}
              accessibilityLabel="📋 Copy Network Logs"
              accessibilityRole="button"
            >
              <Text style={{ color: colors.text.primary, fontSize: 14, fontFamily: font.semibold, flex: 1 }}>
                📋 Copy Network Logs
              </Text>
              <Ionicons name="globe-outline" size={16} color={colors.accent.primary} />
            </TouchableOpacity>

            <TouchableOpacity
              style={{
                backgroundColor: colors.accent.errorDim,
                borderRadius: radius.md,
                padding: space.md,
                flexDirection: 'row',
                alignItems: 'center',
                gap: space.sm,
                borderWidth: 1,
                borderColor: colors.border.subtle,
              }}
              onPress={handleClearLogs}
              accessibilityLabel="🗑️ Clear Logs"
              accessibilityRole="button"
            >
              <Text style={{ color: colors.accent.error, fontSize: 14, fontFamily: font.semibold, flex: 1 }}>
                🗑️ Clear Logs
              </Text>
              <Ionicons name="trash-outline" size={16} color={colors.accent.error} />
            </TouchableOpacity>
          </View>

          {/* Save Button at the bottom */}
          <View style={{ marginTop: space.lg, marginBottom: space.xl }}>
            <TouchableOpacity style={styles.saveButton} onPress={handleSave} accessibilityLabel="Save configuration" accessibilityRole="button">
              <Text style={styles.saveButtonText}>Save Configuration</Text>
            </TouchableOpacity>
            <Text style={{ color: colors.text.tertiary, fontSize: 11, fontFamily: font.regular, textAlign: 'center', marginTop: -space.md, paddingHorizontal: space.xl }}>Saves IP address and device name. Toggles auto-save instantly.</Text>
          </View>

        </Animated.ScrollView>

        {/* Advanced Clipboard Junk Cleaner Modal */}
        <Modal
          visible={showJunkCleaner}
          transparent
          animationType="slide"
          onRequestClose={() => setShowJunkCleaner(false)}
        >
          <View style={{ flex: 1, backgroundColor: 'rgba(0,0,0,0.7)', justifyContent: 'flex-end' }}>
            <View style={{
              backgroundColor: colors.bg.card,
              borderTopLeftRadius: 24,
              borderTopRightRadius: 24,
              padding: 20,
              maxHeight: '85%',
              borderWidth: 1,
              borderColor: colors.border.subtle,
            }}>
              <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', marginBottom: 16 }}>
                <View style={{ flexDirection: 'row', alignItems: 'center', gap: 10 }}>
                  <View style={{ width: 36, height: 36, borderRadius: 12, backgroundColor: colors.accent.primary + '20', alignItems: 'center', justifyContent: 'center' }}>
                    <Ionicons name="sparkles" size={20} color={colors.accent.primary} />
                  </View>
                  <View>
                    <Text style={{ fontSize: 18, fontFamily: font.bold, color: colors.text.primary }}>Junk Cleaner</Text>
                    <Text style={{ fontSize: 12, fontFamily: font.medium, color: colors.text.secondary }}>
                      {totalClipsCount} clips analyzed • {junkPreviewCount ?? '...'} junk detected
                    </Text>
                  </View>
                </View>
                <TouchableOpacity onPress={() => setShowJunkCleaner(false)} style={{ padding: 6 }}>
                  <Ionicons name="close" size={22} color={colors.text.secondary} />
                </TouchableOpacity>
              </View>

              <ScrollView showsVerticalScrollIndicator={false}>
                {/* Age Filter */}
                <Text style={{ fontSize: 12, fontFamily: font.bold, color: colors.text.secondary, textTransform: 'uppercase', marginBottom: 8, marginTop: 4 }}>
                  Delete Items Older Than
                </Text>
                <View style={{ flexDirection: 'row', gap: 6, marginBottom: 16, flexWrap: 'wrap' }}>
                  {(['24h', '3d', '7d', '30d', 'all'] as const).map((filter) => {
                    const label = filter === '24h' ? '24 Hours' : filter === '3d' ? '3 Days' : filter === '7d' ? '7 Days' : filter === '30d' ? '30 Days' : 'Any Age';
                    const isSelected = junkAgeFilter === filter;
                    return (
                      <TouchableOpacity
                        key={filter}
                        onPress={() => { Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); setJunkAgeFilter(filter); }}
                        style={{
                          paddingHorizontal: 12,
                          paddingVertical: 7,
                          borderRadius: 20,
                          backgroundColor: isSelected ? colors.accent.primary : colors.bg.input,
                          borderWidth: 1,
                          borderColor: isSelected ? colors.accent.primary : colors.border.subtle,
                        }}
                      >
                        <Text style={{ fontSize: 12, fontFamily: font.semibold, color: isSelected ? '#FFF' : colors.text.secondary }}>
                          {label}
                        </Text>
                      </TouchableOpacity>
                    );
                  })}
                </View>

                {/* Cleaner Toggles */}
                <Text style={{ fontSize: 12, fontFamily: font.bold, color: colors.text.secondary, textTransform: 'uppercase', marginBottom: 8 }}>
                  Cleanup Options
                </Text>

                <View style={{ backgroundColor: colors.bg.input, borderRadius: radius.md, padding: 12, gap: 12, marginBottom: 16 }}>
                  <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center' }}>
                    <View style={{ flex: 1, paddingRight: 10 }}>
                      <Text style={{ color: colors.text.primary, fontSize: 14, fontFamily: font.semibold }}>Duplicate Clips</Text>
                      <Text style={{ color: colors.text.tertiary, fontSize: 11 }}>Remove identical copied texts or URLs</Text>
                    </View>
                    <Switch value={cleanDuplicates} onValueChange={setCleanDuplicates} thumbColor="#FFF" trackColor={{ false: colors.text.disabled, true: colors.accent.primary }} />
                  </View>

                  <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center' }}>
                    <View style={{ flex: 1, paddingRight: 10 }}>
                      <Text style={{ color: colors.text.primary, fontSize: 14, fontFamily: font.semibold }}>Blank & Whitespace</Text>
                      <Text style={{ color: colors.text.tertiary, fontSize: 11 }}>Purge empty clips or spaces</Text>
                    </View>
                    <Switch value={cleanWhitespace} onValueChange={setCleanWhitespace} thumbColor="#FFF" trackColor={{ false: colors.text.disabled, true: colors.accent.primary }} />
                  </View>

                  <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center' }}>
                    <View style={{ flex: 1, paddingRight: 10 }}>
                      <Text style={{ color: colors.text.primary, fontSize: 14, fontFamily: font.semibold }}>Micro Snippets</Text>
                      <Text style={{ color: colors.text.tertiary, fontSize: 11 }}>Remove 1 or 2 character accidental copies</Text>
                    </View>
                    <Switch value={cleanMicroSnips} onValueChange={setCleanMicroSnips} thumbColor="#FFF" trackColor={{ false: colors.text.disabled, true: colors.accent.primary }} />
                  </View>

                  <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center' }}>
                    <View style={{ flex: 1, paddingRight: 10 }}>
                      <Text style={{ color: colors.text.primary, fontSize: 14, fontFamily: font.semibold }}>Broken File Attachments</Text>
                      <Text style={{ color: colors.text.tertiary, fontSize: 11 }}>Remove clips whose files are missing from storage</Text>
                    </View>
                    <Switch value={cleanBrokenFiles} onValueChange={setCleanBrokenFiles} thumbColor="#FFF" trackColor={{ false: colors.text.disabled, true: colors.accent.primary }} />
                  </View>

                  <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center' }}>
                    <View style={{ flex: 1, paddingRight: 10 }}>
                      <Text style={{ color: colors.accent.success, fontSize: 14, fontFamily: font.semibold }}>Protect Pinned Clips</Text>
                      <Text style={{ color: colors.text.tertiary, fontSize: 11 }}>Never delete your pinned clipboard favorites</Text>
                    </View>
                    <Switch value={protectPinned} onValueChange={setProtectPinned} thumbColor="#FFF" trackColor={{ false: colors.text.disabled, true: colors.accent.success }} />
                  </View>
                </View>

                {/* Clean Button */}
                <TouchableOpacity
                  onPress={executeCleanJunk}
                  style={{
                    backgroundColor: (junkPreviewCount ?? 0) > 0 ? colors.accent.error : colors.bg.input,
                    borderRadius: 14,
                    paddingVertical: 14,
                    alignItems: 'center',
                    marginBottom: 10,
                  }}
                  disabled={(junkPreviewCount ?? 0) === 0}
                >
                  <Text style={{ color: (junkPreviewCount ?? 0) > 0 ? '#FFF' : colors.text.tertiary, fontSize: 15, fontFamily: font.bold }}>
                    {(junkPreviewCount ?? 0) > 0 ? `Clean ${junkPreviewCount} Junk Clips Now` : 'No Junk Found'}
                  </Text>
                </TouchableOpacity>
              </ScrollView>
            </View>
          </View>
        </Modal>
      </KeyboardAvoidingView>
    </View>
    </LinearGradient>
  );
}

const createStyles = (c: Record<string, any>) => StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: 'transparent',
  },
  scrollContent: {
    paddingBottom: 110,
  },

  saveButton: {
    backgroundColor: c.accent.primary,
    paddingVertical: 16,
    borderRadius: radius.lg,
    alignItems: 'center',
    marginHorizontal: space.xl,
    marginBottom: space.xl,
    ...shadows.glow(c.accent.primary),
  },
  saveButtonText: {
    color: '#FFFFFF',
    fontSize: 15,
    fontFamily: font.bold,
    letterSpacing: 0.3,
  },
  card: {
    backgroundColor: c.bg.card,
    marginHorizontal: space.xl,
    borderRadius: radius.xl,
    padding: space['2xl'],
    borderWidth: 1,
    borderColor: c.border.subtle,
    borderTopColor: c.innerHighlight,
    ...shadows.card,
  },
  sectionHeader: {
    color: c.text.primary,
    fontSize: 17,
    fontFamily: font.semibold,
    marginBottom: space.xl,
    letterSpacing: -0.2,
  },
  inputContainer: {
    marginBottom: 10,
  },
  inputHeaderRow: {
    flexDirection: 'row',
    alignItems: 'center',
    marginBottom: space.md,
  },
  inputLabel: {
    color: c.text.primary,
    fontSize: 14,
    fontFamily: font.semibold,
    marginLeft: space.sm,
  },
  input: {
    backgroundColor: c.bg.input,
    color: c.text.primary,
    fontSize: 16,
    fontFamily: font.medium,
    borderRadius: radius.md,
    paddingHorizontal: space.lg,
    paddingVertical: 14,
    borderWidth: 1,
    borderColor: c.border.subtle,
  },
  helperText: {
    color: c.text.tertiary,
    fontSize: 12,
    fontFamily: font.regular,
    marginTop: 10,
    lineHeight: 18,
  },
});

export default function SettingsScreen() {
  return (
    <AppErrorBoundary fallbackTitle="Settings screen crashed">
      <SettingsScreenInner />
    </AppErrorBoundary>
  );
}
