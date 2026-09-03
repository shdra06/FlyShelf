/**
 * NetworkDashboard — Premium Network Monitoring Modal
 *
 * Features:
 *  - SVG-based radar with animated sweep line
 *  - Device dots positioned by latency
 *  - Connection quality cards (horizontal scroll)
 *  - Network stats row
 *  - Speed test with gradient button
 *  - Uses BottomSheet for modal container
 */

import React, { useMemo, useCallback } from 'react';
import {
  View,
  Text,
  ScrollView,
  TouchableOpacity,
  ActivityIndicator,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import Svg, { Circle, Line, G } from 'react-native-svg';
import Animated, {
  useSharedValue,
  useAnimatedProps,
  withRepeat,
  withTiming,
  Easing,
} from 'react-native-reanimated';
import { LinearGradient } from 'expo-linear-gradient';
import BottomSheet from '../components/BottomSheet';
import { useNetworkDashboard, DeviceInfo } from '../features/network/useNetworkDashboard';
import { createNetworkStyles } from '../styles/networkStyles';
import { useAppTheme } from '../hooks/useAppTheme';
import { font, space } from '../styles/theme';

// ═══════════════════════════════════════════
// TYPES
// ═══════════════════════════════════════════

type NetworkDashboardProps = {
  visible: boolean;
  onClose: () => void;
  pcUrl: string | null;
  pairingKey: string | null;
  onPairPress?: () => void;
};

// ═══════════════════════════════════════════
// ANIMATED SVG COMPONENTS
// ═══════════════════════════════════════════

const AnimatedLine = Animated.createAnimatedComponent(Line);

// ═══════════════════════════════════════════
// HELPERS
// ═══════════════════════════════════════════

/** Hash deviceId to get a stable angle for radar positioning */
const hashToAngle = (deviceId: string): number => {
  let hash = 0;
  for (let i = 0; i < deviceId.length; i++) {
    const char = deviceId.charCodeAt(i);
    hash = ((hash << 5) - hash) + char;
    hash |= 0; // Convert to 32-bit integer
  }
  return (Math.abs(hash) % 360) * (Math.PI / 180);
};

/** Get latency quality color */
const getLatencyColor = (latencyMs: number, colors: Record<string, any>): string => {
  if (latencyMs <= 0) return colors.text.disabled;
  if (latencyMs < 50) return colors.accent.success;
  if (latencyMs < 200) return colors.accent.warning;
  return colors.accent.error;
};

/** Relative time string */
const timeAgo = (timestamp: number): string => {
  const diff = Date.now() - timestamp;
  const seconds = Math.floor(diff / 1000);
  if (seconds < 60) return 'Just now';
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.floor(hours / 24)}d ago`;
};

// ═══════════════════════════════════════════
// RADAR COMPONENT
// ═══════════════════════════════════════════

const RADAR_SIZE = 220;
const RADAR_CENTER = RADAR_SIZE / 2;
const RING_RADII = [35, 65, 95];

function DeviceRadar({
  devices,
  colors,
  shadows,
}: {
  devices: DeviceInfo[];
  colors: Record<string, any>;
  shadows: Record<string, any>;
}) {
  // Sweep line angle animation
  const sweepAngle = useSharedValue(0);

  React.useEffect(() => {
    sweepAngle.value = withRepeat(
      withTiming(360, { duration: 4000, easing: Easing.linear }),
      -1, // infinite
      false,
    );
  }, []);

  const sweepLineProps = useAnimatedProps(() => {
    const rad = (sweepAngle.value * Math.PI) / 180;
    const endX = RADAR_CENTER + Math.cos(rad) * RING_RADII[2];
    const endY = RADAR_CENTER + Math.sin(rad) * RING_RADII[2];
    return {
      x2: endX,
      y2: endY,
    };
  });

  return (
    <View style={{ width: RADAR_SIZE, height: RADAR_SIZE, alignItems: 'center', justifyContent: 'center' }}>
      <Svg width={RADAR_SIZE} height={RADAR_SIZE} viewBox={`0 0 ${RADAR_SIZE} ${RADAR_SIZE}`}>
        {/* Concentric rings */}
        {RING_RADII.map((r, i) => (
          <Circle
            key={`ring-${i}`}
            cx={RADAR_CENTER}
            cy={RADAR_CENTER}
            r={r}
            stroke={colors.border.subtle}
            strokeWidth={1}
            fill="none"
          />
        ))}

        {/* Crosshair lines */}
        <Line
          x1={RADAR_CENTER}
          y1={RADAR_CENTER - RING_RADII[2]}
          x2={RADAR_CENTER}
          y2={RADAR_CENTER + RING_RADII[2]}
          stroke={colors.border.subtle}
          strokeWidth={0.5}
          opacity={0.5}
        />
        <Line
          x1={RADAR_CENTER - RING_RADII[2]}
          y1={RADAR_CENTER}
          x2={RADAR_CENTER + RING_RADII[2]}
          y2={RADAR_CENTER}
          stroke={colors.border.subtle}
          strokeWidth={0.5}
          opacity={0.5}
        />

        {/* Animated sweep line */}
        <AnimatedLine
          x1={RADAR_CENTER}
          y1={RADAR_CENTER}
          stroke={colors.accent.primary}
          strokeWidth={1.5}
          opacity={0.6}
          animatedProps={sweepLineProps}
        />

        {/* Device dots */}
        {devices.map((device) => {
          const angle = hashToAngle(device.deviceId);
          const dist = device.isAlive
            ? Math.min(device.latencyMs * 0.5, RING_RADII[2] * 0.9)
            : RING_RADII[2] * 0.85;
          // Ensure minimum distance from center for visibility
          const clampedDist = Math.max(dist, 20);
          const x = RADAR_CENTER + Math.cos(angle) * clampedDist;
          const y = RADAR_CENTER + Math.sin(angle) * clampedDist;

          const dotColor = device.transport === 'lan'
            ? colors.accent.success
            : device.transport === 'cloud'
              ? colors.accent.warning
              : colors.text.disabled;

          return (
            <G key={device.deviceId}>
              {/* Outer glow for alive devices */}
              {device.isAlive && (
                <Circle
                  cx={x}
                  cy={y}
                  r={10}
                  fill={dotColor}
                  opacity={0.15}
                />
              )}
              <Circle
                cx={x}
                cy={y}
                r={6}
                fill={dotColor}
              />
            </G>
          );
        })}

        {/* Center dot (this device) */}
        <Circle
          cx={RADAR_CENTER}
          cy={RADAR_CENTER}
          r={5}
          fill={colors.accent.primary}
        />
      </Svg>

      {/* Device name labels (rendered as RN Text for better font support) */}
      {devices.map((device) => {
        const angle = hashToAngle(device.deviceId);
        const dist = device.isAlive
          ? Math.min(device.latencyMs * 0.5, RING_RADII[2] * 0.9)
          : RING_RADII[2] * 0.85;
        const clampedDist = Math.max(dist, 20);
        const x = RADAR_CENTER + Math.cos(angle) * clampedDist;
        const y = RADAR_CENTER + Math.sin(angle) * clampedDist;

        return (
          <Text
            key={`label-${device.deviceId}`}
            style={{
              position: 'absolute',
              left: x - 25,
              top: y + 10,
              fontFamily: font.medium,
              fontSize: 8,
              color: colors.text.secondary,
              width: 50,
              textAlign: 'center',
            }}
            numberOfLines={1}
          >
            {device.deviceName}
          </Text>
        );
      })}
    </View>
  );
}

// ═══════════════════════════════════════════
// CONNECTION QUALITY CARD
// ═══════════════════════════════════════════

function QualityCard({
  device,
  styles,
  colors,
}: {
  device: DeviceInfo;
  styles: ReturnType<typeof createNetworkStyles>;
  colors: Record<string, any>;
}) {
  const transportStyle = device.transport === 'lan'
    ? styles.transportBadgeLAN
    : device.transport === 'cloud'
      ? styles.transportBadgeCloud
      : styles.transportBadgeOffline;

  const transportTextStyle = device.transport === 'lan'
    ? styles.transportBadgeTextLAN
    : device.transport === 'cloud'
      ? styles.transportBadgeTextCloud
      : styles.transportBadgeTextOffline;

  const latencyColor = getLatencyColor(device.latencyMs, colors);

  const latencyDotStyle = device.latencyMs > 0 && device.latencyMs < 50
    ? styles.latencyDotGood
    : device.latencyMs > 0 && device.latencyMs < 200
      ? styles.latencyDotMedium
      : styles.latencyDotBad;

  return (
    <View style={[styles.qualityCard, device.isAlive && styles.qualityCardOnline]}>
      <Text style={styles.qualityCardName} numberOfLines={1}>
        {device.deviceName}
      </Text>

      <View style={styles.qualityCardTransport}>
        <View style={[styles.transportBadge, transportStyle]}>
          <Text style={[styles.transportBadgeText, transportTextStyle]}>
            {device.transport.toUpperCase()}
          </Text>
        </View>
      </View>

      {device.isAlive && device.latencyMs > 0 && (
        <View style={styles.qualityCardLatency}>
          <View style={[styles.latencyDot, latencyDotStyle]} />
          <Text style={[styles.latencyText, { color: latencyColor }]}>
            {device.latencyMs}ms
          </Text>
        </View>
      )}

      <Text style={styles.qualityCardLastSeen}>
        {device.isAlive ? 'Connected' : `Last seen ${timeAgo(device.lastSeen)}`}
      </Text>
    </View>
  );
}

// ═══════════════════════════════════════════
// MAIN COMPONENT
// ═══════════════════════════════════════════

export default function NetworkDashboard({
  visible,
  onClose,
  pcUrl,
  pairingKey,
  onPairPress,
}: NetworkDashboardProps) {
  const { colors, shadows } = useAppTheme();
  const styles = useMemo(() => createNetworkStyles(colors, shadows), [colors, shadows]);

  const {
    devices,
    stats,
    isRefreshing,
    refresh,
    speedTestResult,
    isSpeedTesting,
    runSpeedTest,
  } = useNetworkDashboard(pcUrl, pairingKey);

  const hasDevices = devices.length > 0;
  const isOnline = stats.devicesOnline > 0;

  const handleRefresh = useCallback(() => {
    refresh();
  }, [refresh]);

  return (
    <BottomSheet visible={visible} onClose={onClose} title="Network Dashboard" maxHeight={0.92}>
      <ScrollView
        style={styles.scrollContent}
        showsVerticalScrollIndicator={false}
        bounces={true}
      >
        {/* ── Header Actions ── */}
        <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', marginBottom: space.md }}>
          <View style={[
            styles.statusBadge,
            !isOnline && styles.statusBadgeOffline,
          ]}>
            <View style={[
              styles.statusBadgeDot,
              !isOnline && styles.statusBadgeDotOffline,
            ]} />
            <Text style={[
              styles.statusBadgeText,
              !isOnline && styles.statusBadgeTextOffline,
            ]}>
              {isOnline ? `${stats.devicesOnline} Online` : 'Offline'}
            </Text>
          </View>

          <TouchableOpacity
            style={styles.refreshBtn}
            onPress={handleRefresh}
            disabled={isRefreshing}
            accessibilityLabel="Refresh network"
            accessibilityRole="button"
          >
            {isRefreshing ? (
              <ActivityIndicator size="small" color={colors.accent.primary} />
            ) : (
              <Ionicons name="refresh" size={18} color={colors.text.secondary} />
            )}
          </TouchableOpacity>
        </View>

        {/* ── Device Radar ── */}
        {hasDevices ? (
          <>
            <View style={styles.radarContainer}>
              <DeviceRadar devices={devices} colors={colors} shadows={shadows} />
            </View>

            {/* ── Connection Quality Cards ── */}
            <View style={styles.qualitySection}>
              <Text style={styles.qualitySectionTitle}>DEVICE CONNECTIONS</Text>
              <ScrollView
                horizontal
                showsHorizontalScrollIndicator={false}
                contentContainerStyle={styles.qualityScrollContent}
              >
                {devices.map((device) => (
                  <QualityCard
                    key={device.deviceId}
                    device={device}
                    styles={styles}
                    colors={colors}
                  />
                ))}
              </ScrollView>
            </View>

            {/* ── Network Stats Row ── */}
            <View style={styles.statsSection}>
              <Text style={styles.statsSectionTitle}>NETWORK STATS</Text>
              <View style={styles.statsRow}>
                <View style={styles.statBadge}>
                  <Text style={[styles.statValue, styles.statValueSuccess]}>
                    {stats.devicesOnline}
                  </Text>
                  <Text style={styles.statLabel}>ONLINE</Text>
                </View>

                <View style={styles.statDivider} />

                <View style={styles.statBadge}>
                  <Text style={styles.statValue}>
                    {stats.connectionType === 'wifi' ? 'WiFi' : stats.connectionType}
                  </Text>
                  <Text style={styles.statLabel}>NETWORK</Text>
                </View>

                <View style={styles.statDivider} />

                <View style={styles.statBadge}>
                  <Text style={styles.statValue}>
                    {stats.wifiName || '—'}
                  </Text>
                  <Text style={styles.statLabel}>SSID</Text>
                </View>

                <View style={styles.statDivider} />

                <View style={styles.statBadge}>
                  <Text style={[styles.statValue, styles.statValueSuccess]}>
                    {stats.bestLatency > 0 ? `${stats.bestLatency}` : '—'}
                  </Text>
                  <Text style={styles.statLabel}>BEST MS</Text>
                </View>
              </View>
            </View>

            {/* ── Speed Test ── */}
            <View style={styles.speedTestSection}>
              <TouchableOpacity
                style={styles.speedTestBtn}
                onPress={runSpeedTest}
                disabled={isSpeedTesting || !pcUrl}
                activeOpacity={0.85}
                accessibilityLabel="Run speed test"
                accessibilityRole="button"
              >
                <LinearGradient
                  colors={['#4A62EB', '#6384FF']}
                  start={{ x: 0, y: 0 }}
                  end={{ x: 1, y: 0 }}
                  style={styles.speedTestBtnGradient}
                >
                  {isSpeedTesting ? (
                    <ActivityIndicator size="small" color="#FFFFFF" />
                  ) : (
                    <Ionicons name="speedometer-outline" size={20} color="#FFFFFF" />
                  )}
                  <Text style={styles.speedTestBtnText}>
                    {isSpeedTesting ? 'Testing...' : 'Run Speed Test'}
                  </Text>
                </LinearGradient>
              </TouchableOpacity>

              {speedTestResult && (
                <View style={styles.speedTestResult}>
                  <Text style={styles.speedTestResultTitle}>SPEED TEST RESULT</Text>
                  <Text style={styles.speedTestResultValue}>
                    {speedTestResult.mbps}
                  </Text>
                  <Text style={styles.speedTestResultUnit}>Mbps</Text>
                  <Text style={styles.speedTestResultLatency}>
                    Latency: {speedTestResult.latencyMs}ms
                  </Text>
                </View>
              )}
            </View>
          </>
        ) : (
          /* ── Empty State ── */
          <View style={styles.emptyState}>
            <Text style={styles.emptyIcon}>📡</Text>
            <Text style={styles.emptyTitle}>No Devices Found</Text>
            <Text style={styles.emptySubtitle}>
              Connect your PC to monitor latency, transfer speeds, and synchronization health.
            </Text>
            {onPairPress && (
              <TouchableOpacity
                style={{
                  marginTop: 18,
                  backgroundColor: colors.accent.primary,
                  paddingHorizontal: 22,
                  paddingVertical: 12,
                  borderRadius: 24,
                  flexDirection: 'row',
                  alignItems: 'center',
                  gap: 8,
                }}
                onPress={onPairPress}
                activeOpacity={0.85}
                accessibilityLabel="Pair a device"
                accessibilityRole="button"
              >
                <Ionicons name="qr-code-outline" size={18} color="#FFF" />
                <Text style={{ color: '#FFF', fontFamily: font.bold, fontSize: 14 }}>
                  Connect a Device
                </Text>
              </TouchableOpacity>
            )}
          </View>
        )}

        {/* Bottom spacer */}
        <View style={{ height: 40 }} />
      </ScrollView>
    </BottomSheet>
  );
}
