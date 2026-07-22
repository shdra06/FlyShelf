/**
 * useNetworkDashboard — Network monitoring & device discovery hook
 *
 * Manages:
 *  - Device list with live health checks
 *  - Network stats (online count, latency, connection type)
 *  - Speed test against PC
 *  - Auto-refresh on 5-second interval
 */

import { useState, useEffect, useRef, useCallback } from 'react';
import { fetchWithTimeout } from '../../utils/networkHelpers';
import { useSettings, PairedDevice } from '../../context/SettingsContext';
import NetInfo, { NetInfoState } from '@react-native-community/netinfo';

// ═══════════════════════════════════════════
// TYPES
// ═══════════════════════════════════════════

export type DeviceInfo = {
  deviceId: string;
  deviceName: string;
  type: 'pc' | 'mobile';
  transport: 'lan' | 'cloud' | 'offline';
  isAlive: boolean;
  latencyMs: number;
  lastSeen: number;
};

export type NetworkStats = {
  devicesOnline: number;
  devicesPaired: number;
  connectionType: string;
  wifiName: string | null;
  bestLatency: number;
  avgLatency: number;
};

export type SpeedTestResult = {
  mbps: number;
  latencyMs: number;
};

// ═══════════════════════════════════════════
// CONSTANTS
// ═══════════════════════════════════════════

const REFRESH_INTERVAL = 5000;
const HEALTH_TIMEOUT = 3000;
const SPEED_TEST_PAYLOAD_SIZE = 1024 * 1024; // 1MB

// ═══════════════════════════════════════════
// HELPERS
// ═══════════════════════════════════════════

/** Probe a single device URL for liveness + latency */
const probeDevice = async (
  url: string,
  pairingKey: string,
): Promise<{ alive: boolean; latencyMs: number }> => {
  const start = Date.now();
  try {
    const res = await fetchWithTimeout(
      `${url}/api/health`,
      {
        method: 'GET',
        headers: {
          'X-Pairing-Key': pairingKey,
          'X-FlyShelf-Client': 'MobileCompanion',
        },
      },
      HEALTH_TIMEOUT,
    );
    if (res.ok) {
      return { alive: true, latencyMs: Date.now() - start };
    }
    return { alive: false, latencyMs: -1 };
  } catch {
    return { alive: false, latencyMs: -1 };
  }
};

/** Map a PairedDevice from settings to a DeviceInfo with health check */
const enrichDevice = async (
  device: PairedDevice,
  pairingKey: string,
): Promise<DeviceInfo> => {
  const url = device.localUrl || device.globalUrl;
  let alive = false;
  let latencyMs = -1;
  let transport: DeviceInfo['transport'] = 'offline';

  if (url) {
    const probe = await probeDevice(url, pairingKey);
    alive = probe.alive;
    latencyMs = probe.latencyMs;

    if (alive) {
      // Determine transport from URL shape
      if (url.includes('trycloudflare.com')) {
        transport = 'cloud';
      } else {
        transport = 'lan';
      }
    }
  }

  // Override with live status from settings if available
  if (device.isOnline && device.connectionType) {
    transport = device.connectionType === 'LAN' ? 'lan'
      : device.connectionType === 'Cloud' ? 'cloud'
      : 'offline';
  }

  return {
    deviceId: device.deviceId,
    deviceName: device.deviceName,
    type: device.deviceType === 'PC' ? 'pc' : 'mobile',
    transport,
    isAlive: alive || (device.isOnline ?? false),
    latencyMs: latencyMs > 0 ? latencyMs : (device.latencyMs ?? -1),
    lastSeen: device.lastSeen ?? Date.now(),
  };
};

/** Calculate aggregate stats from device list */
const calculateStats = (
  devices: DeviceInfo[],
  netState: { type: string; wifiName: string | null },
): NetworkStats => {
  const onlineDevices = devices.filter(d => d.isAlive);
  const latencies = onlineDevices
    .map(d => d.latencyMs)
    .filter(l => l > 0);

  return {
    devicesOnline: onlineDevices.length,
    devicesPaired: devices.length,
    connectionType: netState.type,
    wifiName: netState.wifiName,
    bestLatency: latencies.length > 0 ? Math.min(...latencies) : 0,
    avgLatency: latencies.length > 0
      ? Math.round(latencies.reduce((a, b) => a + b, 0) / latencies.length)
      : 0,
  };
};

// ═══════════════════════════════════════════
// HOOK
// ═══════════════════════════════════════════

export function useNetworkDashboard(pcUrl: string | null, pairingKey: string | null) {
  const { pairedDevices } = useSettings();

  const [devices, setDevices] = useState<DeviceInfo[]>([]);
  const [stats, setStats] = useState<NetworkStats>({
    devicesOnline: 0,
    devicesPaired: 0,
    connectionType: 'unknown',
    wifiName: null,
    bestLatency: 0,
    avgLatency: 0,
  });
  const [isRefreshing, setIsRefreshing] = useState(false);
  const [speedTestResult, setSpeedTestResult] = useState<SpeedTestResult | null>(null);
  const [isSpeedTesting, setIsSpeedTesting] = useState(false);

  // Track network info from NetInfo
  const netStateRef = useRef<{ type: string; wifiName: string | null }>({
    type: 'unknown',
    wifiName: null,
  });

  // ── Refresh: fetch dashboard data or fallback to local health checks ──
  const refresh = useCallback(async () => {
    if (!pairingKey || pairedDevices.length === 0) {
      setDevices([]);
      setStats(prev => ({
        ...prev,
        devicesOnline: 0,
        devicesPaired: 0,
      }));
      return;
    }

    setIsRefreshing(true);

    try {
      // Try the PC dashboard endpoint first
      if (pcUrl) {
        try {
          const res = await fetchWithTimeout(
            `${pcUrl}/api/network/dashboard`,
            {
              headers: {
                'X-Pairing-Key': pairingKey,
                'X-FlyShelf-Client': 'MobileCompanion',
              },
            },
            HEALTH_TIMEOUT,
          );

          if (res.ok) {
            const data = await res.json();

            if (data.peers && Array.isArray(data.peers)) {
              const mapped: DeviceInfo[] = data.peers.map((peer: any) => ({
                deviceId: peer.deviceId || peer.DeviceId || 'unknown',
                deviceName: peer.deviceName || peer.DeviceName || 'Unknown Device',
                type: (peer.deviceType || peer.DeviceType || '').toLowerCase() === 'pc' ? 'pc' : 'mobile',
                transport: peer.transport === 'lan' || peer.connectionType === 'LAN' ? 'lan'
                  : peer.transport === 'cloud' || peer.connectionType === 'Cloud' ? 'cloud'
                  : 'offline',
                isAlive: peer.isOnline ?? peer.IsOnline ?? true,
                latencyMs: peer.latencyMs ?? peer.LatencyMs ?? -1,
                lastSeen: peer.lastSeen ?? peer.LastSeen ?? Date.now(),
              }));

              setDevices(mapped);
              setStats(calculateStats(mapped, netStateRef.current));
              setIsRefreshing(false);
              return;
            }
          }
        } catch {
          // Dashboard endpoint not available, fall through to local probing
        }
      }

      // Fallback: probe each paired device individually
      const enriched = await Promise.all(
        pairedDevices.map(d => enrichDevice(d, pairingKey)),
      );

      setDevices(enriched);
      setStats(calculateStats(enriched, netStateRef.current));
    } catch (err) {
      console.warn('[useNetworkDashboard] Refresh error:', (err as any)?.message || err);
    }

    setIsRefreshing(false);
  }, [pcUrl, pairingKey, pairedDevices]);

  // ── Auto-refresh on interval ──
  useEffect(() => {
    refresh();
    const interval = setInterval(refresh, REFRESH_INTERVAL);
    return () => clearInterval(interval);
  }, [refresh]);

  // ── Speed test ──
  const runSpeedTest = useCallback(async () => {
    if (!pcUrl || !pairingKey) return;

    setIsSpeedTesting(true);
    setSpeedTestResult(null);

    try {
      // Measure latency first
      const latencyStart = Date.now();
      await fetchWithTimeout(
        `${pcUrl}/api/health`,
        {
          headers: {
            'X-Pairing-Key': pairingKey,
            'X-FlyShelf-Client': 'MobileCompanion',
          },
        },
        HEALTH_TIMEOUT,
      );
      const latencyMs = Date.now() - latencyStart;

      // Upload speed test: send 1MB payload
      const payload = new ArrayBuffer(SPEED_TEST_PAYLOAD_SIZE);
      const view = new Uint8Array(payload);
      for (let i = 0; i < view.length; i++) {
        view[i] = i % 256;
      }

      const uploadStart = Date.now();
      await fetchWithTimeout(
        `${pcUrl}/api/speedtest`,
        {
          method: 'POST',
          headers: {
            'X-Pairing-Key': pairingKey,
            'X-FlyShelf-Client': 'MobileCompanion',
            'Content-Type': 'application/octet-stream',
          },
          body: payload,
        },
        15000, // 15s timeout for speed test
      );
      const uploadDuration = (Date.now() - uploadStart) / 1000; // seconds
      const mbps = (SPEED_TEST_PAYLOAD_SIZE * 8) / (uploadDuration * 1_000_000); // Megabits per second

      setSpeedTestResult({
        mbps: Math.round(mbps * 10) / 10,
        latencyMs,
      });
    } catch (err) {
      console.warn('[useNetworkDashboard] Speed test error:', (err as any)?.message || err);
      setSpeedTestResult(null);
    }

    setIsSpeedTesting(false);
  }, [pcUrl, pairingKey]);

  // ── Network info listener ──
  useEffect(() => {
    const unsub = NetInfo.addEventListener((state: NetInfoState) => {
      const wifiName = (state.type === 'wifi' && (state as any).details?.ssid)
        ? (state as any).details.ssid
        : null;

      netStateRef.current = {
        type: state.type,
        wifiName,
      };

      setStats(prev => ({
        ...prev,
        connectionType: state.type,
        wifiName,
      }));
    });

    return () => unsub();
  }, []);

  return {
    devices,
    stats,
    isRefreshing,
    refresh,
    speedTestResult,
    isSpeedTesting,
    runSpeedTest,
  };
}
