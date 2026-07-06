/**
 * DeviceHub — Premium Device Management Modal
 *
 * A feature-rich modal for managing paired devices, viewing live connection
 * status, and managing pairing keys. This replaces the basic device list in
 * Settings with an interactive, animated experience.
 */

import React, { useState, useRef, useEffect, useCallback, useMemo } from 'react';
import {
  Modal,
  ScrollView,
  TouchableOpacity,
  Animated,
  Alert,
  View,
  Text,
  Platform,
} from 'react-native';
import * as Clipboard from 'expo-clipboard';
import * as HapticsModule from 'expo-haptics';
import { LinearGradient } from 'expo-linear-gradient';
import { useSettings, PairedDevice } from '../context/SettingsContext';
import { IconSymbol } from './ui/icon-symbol';
import { deviceStyles as s } from '../styles/deviceStyles';
import { colors, font, typography, spring, timing, space } from '../styles/theme';

// ═══════════════════════════════════════════
// TYPES
// ═══════════════════════════════════════════

export type ActiveDevice = {
  deviceId: string;
  deviceName: string;
  deviceType: 'PC' | 'Mobile' | 'Browser';
  isOnline: boolean;
  connectionType: 'LAN' | 'Cloud' | 'Offline';
  latencyMs?: number;
  localUrl?: string;
  globalUrl?: string;
  GlobalUrl?: string;       // Firebase casing variant
  isPro?: boolean;
  licenseKey?: string;
  lastSeen?: number;
  // Runtime-enriched fields from LAN verification
  _lanVerified?: boolean;
  _lanUrl?: string;
  DeviceType?: string;      // Firebase casing variant
  [key: string]: unknown;   // Allow additional dynamic properties
};

type DeviceHubProps = {
  visible: boolean;
  onClose: () => void;
  activeDevices?: ActiveDevice[];
};

// ═══════════════════════════════════════════
// CONSTANTS
// ═══════════════════════════════════════════

const MAX_DEVICES = 5;

/** Safe haptic wrappers — silently swallow errors on unsupported devices */
const safeHaptic = (style = HapticsModule.ImpactFeedbackStyle.Medium) => {
  try { HapticsModule.impactAsync(style); } catch {}
};
const safeNotification = (type = HapticsModule.NotificationFeedbackType.Success) => {
  try { HapticsModule.notificationAsync(type); } catch {}
};

// ═══════════════════════════════════════════
// HELPERS
// ═══════════════════════════════════════════

/** Mask a pairing key, showing first 4 and last 4 chars */
const maskKey = (key: string): string => {
  if (key.length <= 8) return key;
  return key.slice(0, 4) + '••••••••••••••••••••••••' + key.slice(-4);
};

/** Relative time string from a timestamp */
const timeAgo = (timestamp: number): string => {
  const diff = Date.now() - timestamp;
  const minutes = Math.floor(diff / 60000);
  if (minutes < 1) return 'Just now';
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  return `${days}d ago`;
};

/** Format timestamp to short date */
const formatDate = (timestamp: number): string => {
  const d = new Date(timestamp);
  const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  return `${months[d.getMonth()]} ${d.getDate()}, ${d.getFullYear()}`;
};

/** Get connection quality string from active devices */
const getConnectionQuality = (devices: ActiveDevice[]): { label: string; color: string } => {
  if (devices.length === 0) return { label: 'Offline', color: colors.accent.error };
  const online = devices.filter(d => d.isOnline);
  if (online.length === 0) return { label: 'Offline', color: colors.accent.error };
  const allLan = online.every(d => d.connectionType === 'LAN');
  if (allLan) return { label: 'Excellent', color: colors.accent.success };
  const hasLan = online.some(d => d.connectionType === 'LAN');
  if (hasLan) return { label: 'Good', color: colors.accent.primary };
  return { label: 'Limited', color: colors.accent.warning };
};

// ═══════════════════════════════════════════
// DEVICE CARD COMPONENT
// ═══════════════════════════════════════════

type DeviceCardProps = {
  device: PairedDevice;
  activeInfo?: ActiveDevice;
  index: number;
  onRemove: (id: string, name: string) => void;
};

const DeviceCard = React.memo(function DeviceCard({ device, activeInfo, index, onRemove }: DeviceCardProps) {
  const isOnline = activeInfo?.isOnline ?? false;
  const connectionType = activeInfo?.connectionType ?? 'Offline';
  const latencyMs = activeInfo?.latencyMs;

  // Staggered entrance animation
  const slideAnim = useRef(new Animated.Value(60)).current;
  const opacityAnim = useRef(new Animated.Value(0)).current;

  // Press animation
  const scaleAnim = useRef(new Animated.Value(1)).current;

  useEffect(() => {
    const delay = index * timing.staggerDelay;
    const timer = setTimeout(() => {
      Animated.parallel([
        Animated.spring(slideAnim, {
          toValue: 0,
          damping: spring.gentle.damping,
          stiffness: spring.gentle.stiffness,
          mass: spring.gentle.mass,
          useNativeDriver: true,
        }),
        Animated.timing(opacityAnim, {
          toValue: 1,
          duration: timing.entranceDuration,
          useNativeDriver: true,
        }),
      ]).start();
    }, delay);
    return () => clearTimeout(timer);
  }, []);

  const onPressIn = useCallback(() => {
    Animated.spring(scaleAnim, {
      toValue: 0.98,
      damping: spring.press.damping,
      stiffness: spring.press.stiffness,
      mass: spring.press.mass,
      useNativeDriver: true,
    }).start();
  }, []);

  const onPressOut = useCallback(() => {
    Animated.spring(scaleAnim, {
      toValue: 1,
      damping: spring.bounce.damping,
      stiffness: spring.bounce.stiffness,
      mass: spring.bounce.mass,
      useNativeDriver: true,
    }).start();
  }, []);

  // Device icon based on type
  const deviceIconName = device.deviceType === 'PC'
    ? 'laptopcomputer' as const
    : device.deviceType === 'Mobile'
      ? 'iphone' as const
      : 'globe' as const;

  const iconColor = device.deviceType === 'PC'
    ? colors.accent.info
    : device.deviceType === 'Mobile'
      ? colors.accent.success
      : colors.accent.warning;

  const iconContainerStyle = device.deviceType === 'PC'
    ? s.deviceIconContainerPC
    : device.deviceType === 'Mobile'
      ? s.deviceIconContainerMobile
      : s.deviceIconContainerBrowser;

  // Status dot style
  const statusDotStyle = isOnline
    ? connectionType === 'LAN'
      ? s.statusDotOnline
      : s.statusDotCloud
    : s.statusDotOffline;

  // Connection badge styles
  const connBadgeStyle = connectionType === 'LAN'
    ? s.connectionBadgeLAN
    : connectionType === 'Cloud'
      ? s.connectionBadgeCloud
      : s.connectionBadgeOffline;

  const connBadgeTextStyle = connectionType === 'LAN'
    ? s.connectionBadgeTextLAN
    : connectionType === 'Cloud'
      ? s.connectionBadgeTextCloud
      : s.connectionBadgeTextOffline;

  // Status text
  const statusLabel = isOnline ? 'Online' : 'Offline';
  const lastSeenText = !isOnline && activeInfo?.lastSeen
    ? `Last seen ${timeAgo(activeInfo.lastSeen)}`
    : null;

  return (
    <Animated.View
      style={[
        { transform: [{ translateX: slideAnim }, { scale: scaleAnim }], opacity: opacityAnim },
      ]}
    >
      <TouchableOpacity
        activeOpacity={0.9}
        onPressIn={onPressIn}
        onPressOut={onPressOut}
        style={[
          s.deviceCard,
          isOnline ? s.deviceCardOnline : s.deviceCardOffline,
        ]}
        accessibilityLabel={`${device.deviceName}, ${device.deviceType}, ${statusLabel}${connectionType !== 'Offline' ? `, ${connectionType}` : ''}`}
        accessibilityRole="button"
      >
        {/* Online accent strip */}
        {isOnline && <View style={s.onlineStrip} />}

        <View style={s.deviceCardInner}>
          {/* Device icon */}
          <View style={[s.deviceIconContainer, iconContainerStyle]}>
            <IconSymbol name={deviceIconName} size={24} color={iconColor} />
          </View>

          {/* Device info */}
          <View style={s.deviceInfo}>
            {/* Name row */}
            <View style={s.deviceNameRow}>
              <Text style={s.deviceName} numberOfLines={1}>
                {device.deviceName}
              </Text>
              {device.deviceType === 'PC' && (
                device.isPro ? (
                  <View style={s.proBadge}>
                    <Text style={s.proBadgeText}>PRO</Text>
                  </View>
                ) : (
                  <View style={s.freeBadge}>
                    <Text style={s.freeBadgeText}>FREE</Text>
                  </View>
                )
              )}
            </View>

            {/* Status row */}
            <View style={s.statusRow}>
              <View style={[s.statusDot, statusDotStyle]} />
              <Text style={s.statusText}>{statusLabel}</Text>
              {isOnline && (
                <View style={[s.connectionBadge, connBadgeStyle]}>
                  <Text style={[s.connectionBadgeText, connBadgeTextStyle]}>
                    {connectionType}
                  </Text>
                </View>
              )}
              {isOnline && latencyMs != null && (
                <Text style={s.latencyText}>{latencyMs}ms</Text>
              )}
              {lastSeenText && (
                <Text style={s.deviceMeta}>{lastSeenText}</Text>
              )}
            </View>

            {/* Meta row */}
            <Text style={s.deviceMeta}>
              {device.deviceType} · Paired {formatDate(device.pairedAt)}
            </Text>
          </View>

          {/* Remove button */}
          <TouchableOpacity
            style={s.removeBtn}
            onPress={() => onRemove(device.deviceId, device.deviceName)}
            hitSlop={{ top: 8, bottom: 8, left: 8, right: 8 }}
            accessibilityLabel={`Remove ${device.deviceName}`}
            accessibilityRole="button"
          >
            <IconSymbol name="xmark" size={16} color={colors.accent.error} />
          </TouchableOpacity>
        </View>
      </TouchableOpacity>
    </Animated.View>
  );
});

// ═══════════════════════════════════════════
// DEVICE HUB COMPONENT
// ═══════════════════════════════════════════

export default function DeviceHub({ visible, onClose, activeDevices = [] }: DeviceHubProps) {
  const {
    pairedDevices,
    removePairedDevice,
    pairingKey,
    regeneratePairingKey,
  } = useSettings();

  const [keyExpanded, setKeyExpanded] = useState(false);
  const [keyRevealed, setKeyRevealed] = useState(false);

  // Modal entrance animation
  const modalSlide = useRef(new Animated.Value(1)).current;

  useEffect(() => {
    if (visible) {
      Animated.spring(modalSlide, {
        toValue: 0,
        damping: spring.slow.damping,
        stiffness: spring.slow.stiffness,
        mass: spring.slow.mass,
        useNativeDriver: true,
      }).start();
    } else {
      modalSlide.setValue(1);
      setKeyExpanded(false);
      setKeyRevealed(false);
    }
  }, [visible]);

  // ── Computed values ──
  const onlineCount = useMemo(() =>
    activeDevices.filter(d => d.isOnline).length,
    [activeDevices]
  );

  const quality = useMemo(() =>
    getConnectionQuality(activeDevices),
    [activeDevices]
  );

  const deviceCount = pairedDevices.length;
  const isAtLimit = deviceCount >= MAX_DEVICES;

  // ── Handlers ──

  const handleRemoveDevice = useCallback((deviceId: string, deviceName: string) => {
    safeHaptic(HapticsModule.ImpactFeedbackStyle.Medium);
    Alert.alert(
      'Remove Device',
      `Remove "${deviceName}" from your paired devices? This device will need to be re-paired to connect again.`,
      [
        { text: 'Cancel', style: 'cancel' },
        {
          text: 'Remove',
          style: 'destructive',
          onPress: () => {
            removePairedDevice(deviceId);
            safeNotification(HapticsModule.NotificationFeedbackType.Success);
          },
        },
      ]
    );
  }, [removePairedDevice]);

  const handleCopyKey = useCallback(async () => {
    if (!pairingKey) return;
    await Clipboard.setStringAsync(pairingKey);
    safeHaptic(HapticsModule.ImpactFeedbackStyle.Light);
    Alert.alert('Copied', 'Pairing key copied to clipboard.');
  }, [pairingKey]);

  const handleRegenerateKey = useCallback(() => {
    safeHaptic(HapticsModule.ImpactFeedbackStyle.Medium);
    Alert.alert(
      'Regenerate Key',
      'This will generate a new pairing key. All existing devices will need to re-pair. Are you sure?',
      [
        { text: 'Cancel', style: 'cancel' },
        {
          text: 'Regenerate',
          style: 'destructive',
          onPress: async () => {
            await regeneratePairingKey();
            safeNotification(HapticsModule.NotificationFeedbackType.Success);
          },
        },
      ]
    );
  }, [regeneratePairingKey]);

  const handleAddDevice = useCallback(() => {
    Alert.alert(
      'Add Device',
      'How would you like to pair a new device?',
      [
        {
          text: '📷 Scan QR Code',
          onPress: () => onClose(),
        },
        {
          text: '🔢 Enter Pairing Code',
          onPress: () => onClose(),
        },
        { text: 'Cancel', style: 'cancel' },
      ]
    );
  }, [onClose]);

  const toggleKeySection = useCallback(() => {
    setKeyExpanded(prev => !prev);
    safeHaptic(HapticsModule.ImpactFeedbackStyle.Light);
  }, []);

  // ── Limit bar calculations ──
  const limitFraction = deviceCount / MAX_DEVICES;
  const limitBarFillStyle = isAtLimit
    ? s.limitBarFillFull
    : deviceCount >= 4
      ? s.limitBarFillWarning
      : {};
  const limitTextStyle = isAtLimit
    ? s.limitTextFull
    : deviceCount >= 4
      ? s.limitTextWarning
      : {};

  // ── Render ──

  return (
    <Modal
      visible={visible}
      transparent
      animationType="none"
      statusBarTranslucent
      onRequestClose={onClose}
    >
      <View style={s.modal}>
        <Animated.View
          style={[
            s.container,
            {
              transform: [
                {
                  translateY: modalSlide.interpolate({
                    inputRange: [0, 1],
                    outputRange: [0, 800],
                  }),
                },
              ],
            },
          ]}
        >
          {/* Handle bar */}
          <View style={s.handleBar} />

          {/* Header */}
          <View style={s.header}>
            <View style={s.headerLeft}>
              <Text style={s.headerTitle}>My Devices</Text>
              <View style={s.headerCountBadge}>
                <Text style={s.headerCountText}>{deviceCount}</Text>
              </View>
            </View>
            <TouchableOpacity style={s.closeBtn} onPress={onClose} accessibilityLabel="Close device hub" accessibilityRole="button">
              <IconSymbol name="xmark" size={18} color={colors.text.secondary} />
            </TouchableOpacity>
          </View>

          <ScrollView
            style={s.scrollContent}
            showsVerticalScrollIndicator={false}
            bounces={true}
          >
            {/* ── Status Summary Bar ── */}
            {deviceCount > 0 && (
              <View style={s.statusBar}>
                <View style={s.statusBarItem}>
                  <Text style={[s.statusBarValue, s.statusBarValueOnline]}>
                    {onlineCount}
                  </Text>
                  <Text style={s.statusBarLabel}>ONLINE</Text>
                </View>
                <View style={s.statusBarDivider} />
                <View style={s.statusBarItem}>
                  <Text style={s.statusBarValue}>{deviceCount}</Text>
                  <Text style={s.statusBarLabel}>PAIRED</Text>
                </View>
                <View style={s.statusBarDivider} />
                <View style={s.statusBarItem}>
                  <Text style={[s.statusBarValue, { color: quality.color, fontSize: 14 }]}>
                    {quality.label}
                  </Text>
                  <Text style={s.statusBarLabel}>QUALITY</Text>
                </View>
              </View>
            )}

            {/* ── Device Limit Progress ── */}
            {deviceCount > 0 && (
              <View>
                <View style={s.limitBar}>
                  <View
                    style={[
                      s.limitBarFill,
                      limitBarFillStyle,
                      { width: `${limitFraction * 100}%` },
                    ]}
                  />
                </View>
                <Text style={[s.limitText, limitTextStyle]}>
                  {isAtLimit
                    ? `⚠ Device limit reached (${deviceCount}/${MAX_DEVICES})`
                    : `${deviceCount}/${MAX_DEVICES} devices`}
                </Text>
              </View>
            )}

            {/* ── Device List ── */}
            {deviceCount > 0 ? (
              <View style={{ marginTop: space.lg }}>
                <Text style={s.sectionLabel}>YOUR DEVICES</Text>
                {pairedDevices.map((device, index) => {
                  const activeInfo = activeDevices.find(
                    ad => ad.deviceId === device.deviceId
                  );
                  return (
                    <DeviceCard
                      key={device.deviceId}
                      device={device}
                      activeInfo={activeInfo}
                      index={index}
                      onRemove={handleRemoveDevice}
                    />
                  );
                })}
              </View>
            ) : (
              /* ── Empty State ── */
              <View style={s.emptyState}>
                <Text style={s.emptyIcon}>📱</Text>
                <Text style={s.emptyTitle}>No Devices Paired</Text>
                <Text style={s.emptySubtitle}>
                  Connect your PC or other devices to start syncing clipboard, files, and more.
                </Text>
                <TouchableOpacity
                  style={s.emptyAddBtn}
                  onPress={handleAddDevice}
                  activeOpacity={0.85}
                  accessibilityLabel="Add your first device"
                  accessibilityRole="button"
                >
                  <LinearGradient
                    colors={['#4A62EB', '#6384FF']}
                    start={{ x: 0, y: 0 }}
                    end={{ x: 1, y: 0 }}
                    style={s.emptyAddBtnGradient}
                  >
                    <Text style={s.emptyAddBtnText}>+ Add Your First Device</Text>
                  </LinearGradient>
                </TouchableOpacity>
              </View>
            )}

            {/* ── Pairing Key Section (Collapsible) ── */}
            <View style={[s.keySection, { marginTop: deviceCount > 0 ? space.lg : 0 }]}>
              <TouchableOpacity
                style={s.keySectionHeader}
                onPress={toggleKeySection}
                activeOpacity={0.7}
                accessibilityLabel={`Pairing key section, ${keyExpanded ? 'expanded' : 'collapsed'}`}
                accessibilityRole="button"
              >
                <View style={s.keySectionHeaderLeft}>
                  <IconSymbol
                    name="shield.fill"
                    size={16}
                    color={colors.accent.primary}
                  />
                  <Text style={s.keySectionTitle}>Pairing Key</Text>
                </View>
                <IconSymbol
                  name={keyExpanded ? 'chevron.up' : 'chevron.down'}
                  size={18}
                  color={colors.text.tertiary}
                  style={s.keySectionChevron}
                />
              </TouchableOpacity>

              {keyExpanded && (
                <View style={s.keySectionBody}>
                  {pairingKey ? (
                    <>
                      <Text style={s.keyLabel}>YOUR PAIRING KEY</Text>
                      <TouchableOpacity
                        style={s.keyDisplay}
                        onPress={() => setKeyRevealed(prev => !prev)}
                        activeOpacity={0.7}
                        accessibilityLabel={keyRevealed ? 'Hide pairing key' : 'Reveal pairing key'}
                        accessibilityRole="button"
                      >
                        <Text style={s.keyText} numberOfLines={1}>
                          {keyRevealed ? pairingKey : maskKey(pairingKey)}
                        </Text>
                        <IconSymbol
                          name={keyRevealed ? 'chevron.up' : 'chevron.down'}
                          size={14}
                          color={colors.text.tertiary}
                        />
                      </TouchableOpacity>

                      <View style={s.keyActions}>
                        <TouchableOpacity
                          style={s.keyActionBtn}
                          onPress={handleCopyKey}
                          accessibilityLabel="Copy pairing key"
                          accessibilityRole="button"
                          hitSlop={{ top: 8, bottom: 8, left: 8, right: 8 }}
                        >
                          <IconSymbol
                            name="doc.on.doc"
                            size={18}
                            color={colors.text.secondary}
                          />
                        </TouchableOpacity>
                        <TouchableOpacity
                          style={s.keyActionBtnRegen}
                          onPress={handleRegenerateKey}
                          accessibilityLabel="Regenerate pairing key"
                          accessibilityRole="button"
                          accessibilityHint="All devices will need to re-pair"
                          hitSlop={{ top: 8, bottom: 8, left: 8, right: 8 }}
                        >
                          <IconSymbol
                            name="repeat"
                            size={18}
                            color={colors.accent.warning}
                          />
                        </TouchableOpacity>
                      </View>
                    </>
                  ) : (
                    <View style={s.notPairedBanner}>
                      <Text style={s.notPairedText}>
                        No pairing key set. Pair a device to generate one.
                      </Text>
                    </View>
                  )}
                </View>
              )}
            </View>

            {/* ── Add Device Button ── */}
            {deviceCount > 0 && !isAtLimit && (
              <TouchableOpacity
                style={s.addDeviceBtn}
                onPress={handleAddDevice}
                activeOpacity={0.85}
                accessibilityLabel="Add device"
                accessibilityRole="button"
              >
                <LinearGradient
                  colors={['#4A62EB', '#6384FF']}
                  start={{ x: 0, y: 0 }}
                  end={{ x: 1, y: 0 }}
                  style={s.addDeviceBtnGradient}
                >
                  <IconSymbol name="desktopcomputer" size={20} color="#FFFFFF" />
                  <Text style={s.addDeviceBtnText}>+ Add Device</Text>
                </LinearGradient>
              </TouchableOpacity>
            )}
          </ScrollView>
        </Animated.View>
      </View>
    </Modal>
  );
}
