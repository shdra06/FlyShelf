import React, { useState, useCallback, useEffect, useMemo } from 'react';
import * as Haptics from 'expo-haptics';
import { StyleSheet, View, Text, TextInput, TouchableOpacity, KeyboardAvoidingView, Platform, Alert, Switch, NativeModules, ScrollView, ActivityIndicator, Linking } from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import { Ionicons } from '@expo/vector-icons';
import Animated, { useSharedValue, useAnimatedScrollHandler } from 'react-native-reanimated';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { useSettings, DeviceSyncPrefs } from '../../context/SettingsContext';

import { getSecureItem } from '../../utils/secureStorage';

import Constants from 'expo-constants';
import { font, radius, shadows, space, component } from '../../styles/theme';
import { useAppTheme } from '../../hooks/useAppTheme';

import DeviceHub from '../../components/DeviceHub';
import ScreenHeader from '../../components/ScreenHeader';
import StepSlider from '../../components/StepSlider';

import * as Clipboard from 'expo-clipboard';

const APP_VERSION = Constants.expoConfig?.version || '1.0.0';
const VERSION_URL = 'https://raw.githubusercontent.com/shdra06/FlyShelf/main/version.json';

// StepSlider extracted to components/StepSlider.tsx

export default function SettingsScreen() {
  const { colors } = useAppTheme();
  const styles = useMemo(() => createStyles(colors), [colors]);
  const { pcLocalIp, setPcLocalIp, isGlobalSyncEnabled, setGlobalSyncEnabled, deviceName, setDeviceName, isFloatingBallEnabled, setFloatingBallEnabled, floatingBallSize, setFloatingBallSize, floatingBallAutoHide, setFloatingBallAutoHide, pairedDevices, syncPreferences, setSyncPreference, getSyncPrefsForDevice } = useSettings();
  const [localIpInput, setLocalIpInput] = useState(pcLocalIp);
  const [deviceNameInput, setDeviceNameInput] = useState(deviceName);

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
              try { AdvanceOverlay.setOverlayConfig(floatingBallSize, floatingBallAutoHide); } catch (e) { console.warn('Overlay module error:', e); }
            }
          } catch (e) { console.warn('Overlay module error:', e); }
        } else {
          try { AdvanceOverlay.stopOverlay(); } catch (e) { console.warn('Overlay module error:', e); }
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
      const _ctrl = new AbortController(); setTimeout(() => _ctrl.abort(), 10000);
      const res = await fetch(`${VERSION_URL}?t=${Date.now()}`, { signal: _ctrl.signal });
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
  const scrollHandler = useAnimatedScrollHandler({ onScroll: (e) => { scrollY.value = e.contentOffset.y; } });

  return (
    <LinearGradient colors={[colors.bg.base, colors.bg.baseEnd]} style={{ flex: 1 }}>
    <View style={[styles.container, { backgroundColor: 'transparent' }]}>
      <ScreenHeader title="Settings" subtitle="Configuration" scrollY={scrollY} />
      <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : 'height'} style={{flex: 1}}>
        <Animated.ScrollView contentContainerStyle={styles.scrollContent} keyboardShouldPersistTaps="handled" showsVerticalScrollIndicator={false} onScroll={scrollHandler} scrollEventThrottle={16}>

          {/* Save Button at the top */}
          <TouchableOpacity style={styles.saveButton} onPress={handleSave} accessibilityLabel="Save configuration" accessibilityRole="button">
            <Text style={styles.saveButtonText}>Save Configuration</Text>
          </TouchableOpacity>
          <Text style={{ color: colors.text.tertiary, fontSize: 11, fontFamily: font.regular, textAlign: 'center', marginBottom: space.md, marginTop: -space.md }}>Saves IP address and device name. Toggles auto-save instantly.</Text>

          {/* Networking Card */}
          <View style={styles.card}>
            <Text style={styles.sectionHeader}>Networking</Text>
            
            <View style={styles.inputContainer}>
              <View style={styles.inputHeaderRow}>
                <Ionicons name="wifi" size={20} color={colors.accent.primary} />
                <Text style={styles.inputLabel}>FlyShelf PC API Address</Text>
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
                <Text style={styles.inputLabel}>Device Profile Name</Text>
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
              <Text style={styles.helperText}>If disabled, your clipboard and files will ONLY synchronize when connected locally. Cloud Discovery allows paired devices to find each other over the internet using a lightweight signaling coordinator.</Text>
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

            <TouchableOpacity
              style={{
                backgroundColor: colors.accent.errorDim,
                borderRadius: radius.md,
                padding: space.lg,
                marginTop: space.sm,
                flexDirection: 'row',
                alignItems: 'center',
                gap: space.sm,
                borderWidth: 1,
                borderColor: colors.border.subtle,
              }}
              onPress={() => {
                Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
                Alert.alert(
                  'Clear Cache',
                  'This will clear cached clipboard items, notes, and todos. Your settings and paired devices will not be affected.',
                  [
                    { text: 'Cancel', style: 'cancel' },
                    {
                      text: 'Clear',
                      style: 'destructive',
                      onPress: async () => {
                        try {
                          const allKeys = await AsyncStorage.getAllKeys();
                          const cacheKeys = allKeys.filter(k => k.startsWith('@flyshelf_'));
                          if (cacheKeys.length > 0) {
                            await AsyncStorage.multiRemove(cacheKeys);
                          }
                          Alert.alert('Done', `Cleared ${cacheKeys.length} cached item${cacheKeys.length !== 1 ? 's' : ''}.`);
                        } catch (e: any) {
                          Alert.alert('Error', e?.message || 'Failed to clear cache.');
                        }
                      },
                    },
                  ]
                );
              }}
              accessibilityLabel="Clear cache"
              accessibilityRole="button"
            >
              <Ionicons name="trash-outline" size={18} color={colors.accent.error} />
              <Text style={{ color: colors.accent.error, fontSize: 14, fontFamily: font.semibold }}>Clear Cache</Text>
            </TouchableOpacity>

            <Text style={[styles.helperText, { marginTop: space.md }]}>
              Clears locally cached clipboard items, notes, and todos. Does not affect your settings, paired devices, or cloud data.
            </Text>
          </View>

          {/* Bottom padding handled by scrollContent paddingBottom */}

        </Animated.ScrollView>
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

