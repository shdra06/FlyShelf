import React, { useState, useCallback, useEffect } from 'react';
import * as Haptics from 'expo-haptics';
import { StyleSheet, View, Text, TextInput, TouchableOpacity, SafeAreaView, KeyboardAvoidingView, Platform, Alert, Switch, NativeModules, ScrollView, ActivityIndicator } from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import { useSettings } from '../../context/SettingsContext';
import { IconSymbol } from '@/components/ui/icon-symbol';
import * as FileSystem from 'expo-file-system/legacy';
import { getSecureItem } from '../../utils/secureStorage';
import * as IntentLauncher from 'expo-intent-launcher';
import Constants from 'expo-constants';
import { colors, font, radius, shadows, space } from '../../styles/theme';
import AnimatedPressable from '../../components/AnimatedPressable';
import DeviceHub from '../../components/DeviceHub';
import { getDebugLogs, clearDebugLogs, getNetworkLogs, getNetworkLogsText, clearNetworkLogs, onNetworkLogChange, getNetworkLogCount } from '../../utils/debugLog';
import * as Clipboard from 'expo-clipboard';

const APP_VERSION = Constants.expoConfig?.version || '1.0.0';
const VERSION_URL = 'https://raw.githubusercontent.com/shdra06/FlyShelf/main/version.json';

// Custom pure-JS slider row
const StepSlider = ({ value, min, max, step, onValueChange, trackColor, thumbColor, label }: { value: number; min: number; max: number; step: number; onValueChange: (v: number) => void; trackColor: string; thumbColor: string; label: string }) => {
  const pct = Math.max(0, Math.min(100, ((value - min) / (max - min)) * 100));
  return (
    <View style={{marginTop: 8}}>
      <View style={{flexDirection: 'row', alignItems: 'center', gap: 10}}>
        <TouchableOpacity onPress={() => { if (value - step >= min) onValueChange(value - step); }} style={{width: 36, height: 36, borderRadius: 10, backgroundColor: '#2A2F3A', alignItems: 'center', justifyContent: 'center'}}>
          <Text style={{color: '#FFF', fontSize: 18, fontWeight: '800'}}>−</Text>
        </TouchableOpacity>
        <View style={{flex: 1, height: 8, backgroundColor: '#2A2F3A', borderRadius: 4, overflow: 'hidden'}}>
          <View style={{width: `${pct}%`, height: '100%', backgroundColor: trackColor, borderRadius: 4}} />
        </View>
        <TouchableOpacity onPress={() => { if (value + step <= max) onValueChange(value + step); }} style={{width: 36, height: 36, borderRadius: 10, backgroundColor: '#2A2F3A', alignItems: 'center', justifyContent: 'center'}}>
          <Text style={{color: '#FFF', fontSize: 18, fontWeight: '800'}}>+</Text>
        </TouchableOpacity>
      </View>
    </View>
  );
};

export default function SettingsScreen() {
  const { pcLocalIp, setPcLocalIp, isGlobalSyncEnabled, setGlobalSyncEnabled, deviceName, setDeviceName, isFloatingBallEnabled, setFloatingBallEnabled, floatingBallSize, setFloatingBallSize, floatingBallAutoHide, setFloatingBallAutoHide, pairedDevices, removePairedDevice, pairingKey, regeneratePairingKey } = useSettings();
  const [localIpInput, setLocalIpInput] = useState(pcLocalIp);
  const [globalSyncInput, setGlobalSyncInput] = useState(isGlobalSyncEnabled);
  const [deviceNameInput, setDeviceNameInput] = useState(deviceName);
  const [floatingBallInput, setFloatingBallInput] = useState(isFloatingBallEnabled);
  const [showDeviceHub, setShowDeviceHub] = useState(false);

  // ═══ Update System State ═══
  const [updateStatus, setUpdateStatus] = useState<'idle' | 'checking' | 'available' | 'error'>('idle');
  const [latestVersion, setLatestVersion] = useState('');
  const [changelog, setChangelog] = useState('');
  const [downloadUrl, setDownloadUrl] = useState('');

  const { AdvanceOverlay } = NativeModules;

  // ═══ Network Log Viewer State ═══
  const [showNetLogs, setShowNetLogs] = useState(false);
  const [netLogEntries, setNetLogEntries] = useState<string[]>([]);
  const [netLogCount, setNetLogCount] = useState(0);

  useEffect(() => {
    // Subscribe to network log changes for real-time updates
    const unsub = onNetworkLogChange(() => {
      if (showNetLogs) {
        setNetLogEntries(getNetworkLogs().slice(0, 100));
      }
      setNetLogCount(getNetworkLogCount());
    });
    setNetLogCount(getNetworkLogCount());
    return unsub;
  }, [showNetLogs]);

  useEffect(() => {
    if (showNetLogs) {
      setNetLogEntries(getNetworkLogs().slice(0, 100));
    }
  }, [showNetLogs]);



  const handleSave = async () => {
    try {
      await setPcLocalIp(localIpInput);
      await setGlobalSyncEnabled(globalSyncInput);
      await setDeviceName(deviceNameInput);

      if (Platform.OS === 'android' && AdvanceOverlay) {
        if (floatingBallInput) {
          const hasPerm = await AdvanceOverlay.checkOverlayPermission();
          if (!hasPerm) {
             await AdvanceOverlay.requestOverlayPermission();
             Alert.alert('Permission Required', 'Please enable Draw Over Other Apps in settings, switch back, and press save again.');
             return;
          } else {
             AdvanceOverlay.startOverlay();
             AdvanceOverlay.setOverlayConfig(floatingBallSize, floatingBallAutoHide);
          }
        } else {
          AdvanceOverlay.stopOverlay();
        }
      }

      await setFloatingBallEnabled(floatingBallInput);
      Alert.alert('Saved', 'Configuration preserved.');
    } catch (e: any) {
      Alert.alert('Error', e?.message || 'Failed to save settings.');
    }
  };

  // ═══ Update Functions ═══
  const checkForUpdate = useCallback(async () => {
    try {
      setUpdateStatus('checking');
      const res = await fetch(`${VERSION_URL}?t=${Date.now()}`, { signal: AbortSignal.timeout(10000) });
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
                  const { Linking } = require('react-native');
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
            <ActivityIndicator size="small" color="#FFF" />
            <Text style={{ color: '#FFF', fontWeight: '700', fontSize: 13 }}>Checking...</Text>
          </View>
        );
      case 'available':
        return <Text style={{ color: '#FFF', fontWeight: '700', fontSize: 13 }}>Download v{latestVersion}</Text>;
      case 'error':
        return <Text style={{ color: '#FFF', fontWeight: '700', fontSize: 13 }}>Retry Check</Text>;
      default:
        return <Text style={{ color: '#FFF', fontWeight: '700', fontSize: 13 }}>Check Updates</Text>;
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
          const { Linking } = require('react-native');
          Linking.openURL(downloadUrl).catch(() => Alert.alert('Error', 'Could not open update link.'));
        }
        break;
    }
  };

  const getUpdateButtonColor = () => {
    switch (updateStatus) {
      case 'available': return '#F59E0B';
      case 'error': return '#EF4444';
      default: return '#10B981';
    }
  };

  return (
    <LinearGradient colors={[colors.bg.base, colors.bg.baseEnd]} style={{ flex: 1 }}>
    <SafeAreaView style={[styles.container, { backgroundColor: 'transparent' }]}>
      <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : 'height'} style={{flex: 1}}>
        <ScrollView contentContainerStyle={styles.scrollContent} keyboardShouldPersistTaps="handled" showsVerticalScrollIndicator={false}>
          
          <View style={styles.header}>
            <Text style={styles.title}>Settings</Text>
            <Text style={styles.subtitle}>Configure Sync Variables</Text>
          </View>

          {/* Save Button at the top */}
          <TouchableOpacity style={styles.saveButton} onPress={handleSave}>
            <Text style={styles.saveButtonText}>Save Configuration</Text>
          </TouchableOpacity>

          {/* Networking Card */}
          <View style={styles.card}>
            <Text style={styles.sectionHeader}>Networking</Text>
            
            <View style={styles.inputContainer}>
              <View style={styles.inputHeaderRow}>
                <IconSymbol name="network" size={20} color="#4A62EB" />
                <Text style={styles.inputLabel}>FlyShelf PC API Address</Text>
              </View>
              <TextInput
                style={styles.input}
                value={localIpInput}
                onChangeText={setLocalIpInput}
                placeholder="e.g. 192.168.1.5:8999"
                placeholderTextColor="#4C5361"
                keyboardType="numbers-and-punctuation"
              />
              <Text style={styles.helperText}>Fallback IP for direct LAN transfers when your PC isn't auto-detected. If your PC shows up in Active Devices, this can be left blank. Format: 192.168.x.x:8999</Text>
            </View>

            <View style={[styles.inputContainer, { marginTop: 20 }]}>
              <View style={styles.inputHeaderRow}>
                <IconSymbol name="iphone" size={20} color="#4A62EB" />
                <Text style={styles.inputLabel}>Device Profile Name</Text>
              </View>
              <TextInput
                style={styles.input}
                value={deviceNameInput}
                onChangeText={setDeviceNameInput}
                placeholder="e.g. John's Mobile Profile"
                placeholderTextColor="#4C5361"
              />
              <Text style={styles.helperText}>This name identifies you on the clipboard feed.</Text>
            </View>



            <View style={[styles.inputContainer, { marginTop: 20 }]}>
              <View style={{flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center'}}>
                  <View style={styles.inputHeaderRow}>
                    <IconSymbol name="cloud" size={20} color="#4A62EB" />
                    <Text style={styles.inputLabel}>Cloud Discovery</Text>
                  </View>
                  <Switch 
                    value={globalSyncInput} 
                    onValueChange={setGlobalSyncInput} 
                    trackColor={{ false: "#2A2F3A", true: "#4A62EB" }} 
                    thumbColor="#FFF"
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
            >
              <View style={{ flexDirection: 'row', alignItems: 'center' }}>
                <View style={{
                  width: 44, height: 44, borderRadius: 12,
                  backgroundColor: colors.accent.primaryDim,
                  justifyContent: 'center', alignItems: 'center', marginRight: space.md,
                }}>
                  <IconSymbol name="laptopcomputer.and.iphone" size={22} color={colors.accent.primary} />
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
                <IconSymbol name="chevron.right" size={16} color={colors.text.tertiary} />
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
                      <IconSymbol 
                        name={device.deviceType === 'PC' ? 'laptopcomputer' : device.deviceType === 'Mobile' ? 'iphone' : 'globe'}
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

          {/* Floating Clipboard Card */}
          <View style={[styles.card, { marginTop: 16 }]}>
            <Text style={styles.sectionHeader}>Floating Clipboard</Text>

            <View style={styles.inputContainer}>
              <View style={{flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center'}}>
                  <View style={styles.inputHeaderRow}>
                    <IconSymbol name="eye" size={20} color="#8B5CF6" />
                    <Text style={styles.inputLabel}>Enable Floating Ball</Text>
                  </View>
                  <Switch 
                    value={floatingBallInput} 
                    onValueChange={setFloatingBallInput} 
                    trackColor={{ false: "#2A2F3A", true: "#8B5CF6" }} 
                    thumbColor="#FFF"
                  />
              </View>
              <Text style={styles.helperText}>Enable the persistent floating ball on your screen for instant overlay clipboard access anywhere.</Text>
            </View>

            <View style={[styles.inputContainer, { marginTop: 16 }]}>
              <View style={styles.inputHeaderRow}>
                <IconSymbol name="arrow.up.left.and.arrow.down.right" size={20} color="#10B981" />
                <Text style={styles.inputLabel}>Ball Size: {floatingBallSize}dp</Text>
              </View>
              <StepSlider
                value={floatingBallSize}
                min={32}
                max={72}
                step={4}
                onValueChange={(val) => setFloatingBallSize(val)}
                trackColor="#10B981"
                thumbColor="#10B981"
                label="size"
              />
              <Text style={styles.helperText}>Controls how large the floating ball appears on screen. Default: 48dp.</Text>
            </View>

            <View style={[styles.inputContainer, { marginTop: 16 }]}>
              <View style={styles.inputHeaderRow}>
                <IconSymbol name="timer" size={20} color="#F59E0B" />
                <Text style={styles.inputLabel}>Auto-Hide Delay: {(floatingBallAutoHide / 1000).toFixed(1)}s</Text>
              </View>
              <StepSlider
                value={floatingBallAutoHide}
                min={1000}
                max={10000}
                step={500}
                onValueChange={(val) => setFloatingBallAutoHide(val)}
                trackColor="#F59E0B"
                thumbColor="#F59E0B"
                label="delay"
              />
              <Text style={styles.helperText}>Time before the ball auto-hides to the edge. Default: 3 seconds.</Text>
            </View>

            <View style={[styles.inputContainer, { marginTop: 16 }]}>
              <Text style={[styles.helperText, { color: '#6366F1', fontWeight: '500' }]}>
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
                    borderRadius: 12,
                    minWidth: 130,
                    alignItems: 'center',
                  }}
                  onPress={handleUpdatePress}
                  disabled={updateStatus === 'checking'}
                >
                  {getUpdateButtonContent()}
                </TouchableOpacity>
              </View>

              {/* Update Available Info */}
              {updateStatus === 'available' && (
                <View style={{
                  marginTop: 12,
                  backgroundColor: '#1A1D24',
                  borderRadius: 12,
                  padding: 14,
                  borderWidth: 1,
                  borderColor: '#F59E0B33',
                }}>
                  <Text style={{ color: '#F59E0B', fontWeight: '700', fontSize: 14, marginBottom: 4 }}>
                    🎉 Update v{latestVersion} Available
                  </Text>
                  <Text style={{ color: '#8A8F98', fontSize: 12, lineHeight: 18 }}>
                    {changelog}
                  </Text>
                </View>
              )}

              <Text style={[styles.helperText, { marginTop: 12 }]}>
                Checks for the latest updates of FlyShelf and opens the direct download link in your device's web browser, allowing Android to securely install the update without needing sensitive package installer permissions.
              </Text>
            </View>
          </View>

          {/* Network Log Viewer */}
          <View style={{ backgroundColor: '#141824', borderRadius: 20, padding: 20, marginBottom: 16, borderWidth: 1, borderColor: 'rgba(255,255,255,0.04)' }}>
            {/* Header row with toggle */}
            <TouchableOpacity
              onPress={() => setShowNetLogs(!showNetLogs)}
              style={{ flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between' }}
            >
              <View style={{ flexDirection: 'row', alignItems: 'center', gap: 10 }}>
                <Text style={{ fontSize: 18 }}>🌐</Text>
                <Text style={{ color: '#F0F2F5', fontSize: 16, fontWeight: '700' }}>Network Logs</Text>
                {netLogCount > 0 && (
                  <View style={{ backgroundColor: '#6366F133', borderRadius: 10, paddingHorizontal: 8, paddingVertical: 2 }}>
                    <Text style={{ color: '#6366F1', fontSize: 11, fontWeight: '700' }}>{netLogCount}</Text>
                  </View>
                )}
              </View>
              <Text style={{ color: '#6B7280', fontSize: 18 }}>{showNetLogs ? '▲' : '▼'}</Text>
            </TouchableOpacity>

            {/* Action buttons — row 1 */}
            <View style={{ flexDirection: 'row', gap: 8, marginTop: 12 }}>
              <TouchableOpacity
                style={{ flex: 1, backgroundColor: '#1E2330', borderRadius: 12, padding: 12, alignItems: 'center', borderWidth: 1, borderColor: '#2A2F3A' }}
                onPress={async () => {
                  const logs = getNetworkLogsText();
                  if (!logs) { Alert.alert('No Logs', 'No network activity logged yet.'); return; }
                  await Clipboard.setStringAsync(logs);
                  Alert.alert('Copied!', `${logs.split('\n').length} network log entries copied.`);
                }}
              >
                <Text style={{ color: '#60A5FA', fontWeight: '700', fontSize: 12 }}>📋 Copy</Text>
              </TouchableOpacity>
              <TouchableOpacity
                style={{ flex: 1, backgroundColor: '#1E2330', borderRadius: 12, padding: 12, alignItems: 'center', borderWidth: 1, borderColor: '#2A2F3A' }}
                onPress={async () => {
                  const allLogs = getDebugLogs();
                  if (!allLogs) { Alert.alert('No Logs', 'No activity logged yet.'); return; }
                  await Clipboard.setStringAsync(allLogs);
                  Alert.alert('Copied!', `${allLogs.split('\n').length} total log entries copied.`);
                }}
              >
                <Text style={{ color: '#8B5CF6', fontWeight: '700', fontSize: 12 }}>📋 All Logs</Text>
              </TouchableOpacity>
              <TouchableOpacity
                style={{ backgroundColor: '#1E2330', borderRadius: 12, padding: 12, alignItems: 'center', borderWidth: 1, borderColor: '#2A2F3A', paddingHorizontal: 16 }}
                onPress={() => {
                  clearNetworkLogs();
                  clearDebugLogs();
                  setNetLogEntries([]);
                  setNetLogCount(0);
                  Alert.alert('Cleared', 'All logs cleared.');
                }}
              >
                <Text style={{ color: '#EF4444', fontWeight: '700', fontSize: 12 }}>🗑️</Text>
              </TouchableOpacity>
            </View>

            {/* Send to PC Dashboard button */}
            <TouchableOpacity
              style={{ backgroundColor: '#1A2744', borderRadius: 12, padding: 14, alignItems: 'center', borderWidth: 1, borderColor: '#1E3A5F', marginTop: 8 }}
              onPress={async () => {
                try {
                  const logs = getNetworkLogs();
                  if (logs.length === 0) { Alert.alert('No Logs', 'No network logs to send.'); return; }
                  const [localUrl, globalUrl, pk] = await Promise.all([
                    getSecureItem('pairedLocalUrl'),
                    getSecureItem('pairedGlobalUrl'),
                    getSecureItem('pairingKey'),
                  ]);
                  const candidates = [localUrl, globalUrl].filter(u => u && u.startsWith('http')) as string[];
                  if (candidates.length === 0) { Alert.alert('No PC', 'No paired PC URL found. Pair with a PC first.'); return; }
                  let sent = false;
                  for (const url of candidates) {
                    try {
                      const headers: any = { 'Content-Type': 'application/json', 'X-FlyShelf-Client': 'MobileCompanion', 'X-Device-Name': deviceName || 'Mobile' };
                      if (pk) headers['X-Pairing-Key'] = pk;
                      const ctrl = new AbortController();
                      const timer = setTimeout(() => ctrl.abort(), 6000);
                      const res = await fetch(`${url}/api/logs`, { method: 'POST', headers, body: JSON.stringify(logs), signal: ctrl.signal });
                      clearTimeout(timer);
                      if (res.ok) { sent = true; break; }
                    } catch {}
                  }
                  if (sent) {
                    Alert.alert('Sent! ✅', `${logs.length} log entries sent to PC dashboard.\n\nView at: your-pc/logs?pin=YOUR_PIN`);
                  } else {
                    Alert.alert('Failed', 'Could not reach PC. Make sure FlyShelf is running on PC.');
                  }
                } catch (e: any) { Alert.alert('Error', e?.message || 'Unknown error'); }
              }}
            >
              <Text style={{ color: '#3B82F6', fontWeight: '700', fontSize: 13 }}>📤 Send Logs to PC Dashboard</Text>
            </TouchableOpacity>

            {/* Inline log viewer */}
            {showNetLogs && (
              <View style={{
                marginTop: 14,
                backgroundColor: '#0B0E14',
                borderRadius: 14,
                borderWidth: 1,
                borderColor: '#1A1F2E',
                maxHeight: 350,
                overflow: 'hidden',
              }}>
                {/* Log header bar */}
                <View style={{
                  flexDirection: 'row',
                  justifyContent: 'space-between',
                  alignItems: 'center',
                  paddingHorizontal: 14,
                  paddingVertical: 8,
                  backgroundColor: '#10131A',
                  borderBottomWidth: 1,
                  borderBottomColor: '#1A1F2E',
                }}>
                  <Text style={{ color: '#4B5563', fontSize: 10, fontFamily: Platform.OS === 'ios' ? 'Menlo' : 'monospace', fontWeight: '600' }}>
                    LIVE NETWORK FEED — {netLogEntries.length} entries
                  </Text>
                  <TouchableOpacity onPress={() => setNetLogEntries(getNetworkLogs().slice(0, 100))}>
                    <Text style={{ color: '#6366F1', fontSize: 10, fontWeight: '700' }}>↻ Refresh</Text>
                  </TouchableOpacity>
                </View>

                <ScrollView
                  style={{ maxHeight: 300, paddingHorizontal: 12, paddingVertical: 8 }}
                  showsVerticalScrollIndicator={true}
                  nestedScrollEnabled={true}
                >
                  {netLogEntries.length === 0 ? (
                    <Text style={{ color: '#374151', fontSize: 12, fontStyle: 'italic', textAlign: 'center', paddingVertical: 20, fontFamily: Platform.OS === 'ios' ? 'Menlo' : 'monospace' }}>
                      No network activity yet.{'\n'}Sync events will appear here in real-time.
                    </Text>
                  ) : (
                    netLogEntries.map((entry, idx) => {
                      // Color-code by content
                      let textColor = '#6B7280';
                      const upper = entry.toUpperCase();
                      if (upper.includes('ERROR') || upper.includes('FAIL') || upper.includes('✗')) textColor = '#EF4444';
                      else if (upper.includes('FIREBASE') || upper.includes('CLOUDFLARE') || upper.includes('CF_')) textColor = '#F59E0B';
                      else if (upper.includes('DOWNLOAD') || upper.includes('DL-QUEUE') || upper.includes('✓') || upper.includes('✅')) textColor = '#10B981';
                      else if (upper.includes('HTTP') || upper.includes('PC-POLL') || upper.includes('CONNECT')) textColor = '#60A5FA';
                      else if (upper.includes('PAIR') || upper.includes('AUTH')) textColor = '#A78BFA';
                      else if (upper.includes('SCREENSHOT') || upper.includes('MEDIA')) textColor = '#EC4899';

                      return (
                        <Text
                          key={idx}
                          style={{
                            color: textColor,
                            fontSize: 10,
                            fontFamily: Platform.OS === 'ios' ? 'Menlo' : 'monospace',
                            lineHeight: 16,
                            marginBottom: 2,
                          }}
                          selectable={true}
                        >
                          {entry}
                        </Text>
                      );
                    })
                  )}
                </ScrollView>
              </View>
            )}

            <Text style={styles.helperText}>
              Network-only logs: Firebase sync, HTTP requests, Cloudflare, downloads, pairing. Tap header to {showNetLogs ? 'collapse' : 'expand'} the live viewer.
            </Text>
          </View>

          {/* Bottom padding so scroll doesn't cut off behind tab bar */}
          <View style={{ height: 100 }} />

        </ScrollView>
      </KeyboardAvoidingView>
    </SafeAreaView>
    </LinearGradient>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: 'transparent',
  },
  scrollContent: {
    paddingBottom: 110,
  },
  header: {
    paddingTop: 60,
    paddingHorizontal: space['2xl'],
    marginBottom: space.xl,
  },
  title: {
    fontSize: 30,
    fontFamily: font.extrabold,
    color: colors.text.primary,
    letterSpacing: -0.8,
  },
  subtitle: {
    fontSize: 13,
    fontFamily: font.medium,
    color: colors.text.tertiary,
    marginTop: 4,
    textTransform: 'uppercase',
    letterSpacing: 1.5,
  },
  saveButton: {
    backgroundColor: colors.accent.primary,
    paddingVertical: 16,
    borderRadius: radius.lg,
    alignItems: 'center',
    marginHorizontal: space.xl,
    marginBottom: space.xl,
    ...shadows.glow(colors.accent.primary),
  },
  saveButtonText: {
    color: '#FFFFFF',
    fontSize: 15,
    fontFamily: font.bold,
    letterSpacing: 0.3,
  },
  card: {
    backgroundColor: colors.bg.card,
    marginHorizontal: space.xl,
    borderRadius: radius.xl,
    padding: space['2xl'],
    borderWidth: 1,
    borderColor: colors.border.subtle,
    borderTopColor: colors.innerHighlight,
    ...shadows.card,
  },
  sectionHeader: {
    color: colors.text.primary,
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
    color: colors.text.primary,
    fontSize: 14,
    fontFamily: font.semibold,
    marginLeft: space.sm,
  },
  input: {
    backgroundColor: colors.bg.input,
    color: colors.text.primary,
    fontSize: 16,
    fontFamily: font.medium,
    borderRadius: radius.md,
    paddingHorizontal: space.lg,
    paddingVertical: 14,
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },
  helperText: {
    color: colors.text.tertiary,
    fontSize: 12,
    fontFamily: font.regular,
    marginTop: 10,
    lineHeight: 18,
  },
});

