// ═══════════════════════════════════════════════════════════════
// useLanPresence — Makes Android device visible to paired PCs on LAN
// Periodic peer_announce to paired PCs + stores paired device credentials
// ═══════════════════════════════════════════════════════════════
import { useEffect, useRef } from 'react';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { fetchWithTimeout } from '../utils/networkHelpers';
import { syncLog } from '../utils/debugLog';
import { NetworkClock } from '../utils/networkClock';
import NetInfo from '@react-native-community/netinfo';

const PRESENCE_INTERVAL = 15_000; // 15s
const PAIRED_DEVICES_KEY = '@flyshelf_paired_devices_local';

export interface LocalPairedDevice {
  deviceId: string;
  deviceName: string;
  deviceType: string;
  pairingKey: string;
  lastKnownIps: string[];  // e.g. ["192.168.1.5:8999"]
  cloudflareUrl: string;
  lastSeen: number;
  pin?: string;
}

// ═══ Paired Device Credential Storage ═══

export const getLocalPairedDevices = async (): Promise<Record<string, LocalPairedDevice>> => {
  try {
    const raw = await AsyncStorage.getItem(PAIRED_DEVICES_KEY);
    return raw ? JSON.parse(raw) : {};
  } catch { return {}; }
};

export const saveLocalPairedDevice = async (device: LocalPairedDevice): Promise<void> => {
  try {
    const existing = await getLocalPairedDevices();
    existing[device.deviceId] = device;
    await AsyncStorage.setItem(PAIRED_DEVICES_KEY, JSON.stringify(existing));
    syncLog('LAN-PRESENCE', `Saved paired device: ${device.deviceName} (${device.lastKnownIps.join(', ')})`);
  } catch {}
};

export const updatePairedDeviceIp = async (deviceId: string, ip: string): Promise<void> => {
  try {
    const devices = await getLocalPairedDevices();
    const device = devices[deviceId];
    if (!device) return;
    // Add IP to front if not already there
    const cleanIp = ip.replace(/^https?:\/\//, '').replace(/\/$/, '');
    device.lastKnownIps = [cleanIp, ...device.lastKnownIps.filter(i => i !== cleanIp)].slice(0, 5);
    device.lastSeen = NetworkClock.now();
    devices[deviceId] = device;
    await AsyncStorage.setItem(PAIRED_DEVICES_KEY, JSON.stringify(devices));
  } catch {}
};

export const removeLocalPairedDevice = async (deviceId: string): Promise<void> => {
  try {
    const devices = await getLocalPairedDevices();
    delete devices[deviceId];
    await AsyncStorage.setItem(PAIRED_DEVICES_KEY, JSON.stringify(devices));
  } catch {}
};

// ═══ LAN Pairing Function ═══

/** Attempt to pair with a PC over LAN by scanning the subnet for /api/pair_verify?code=X */
export const tryLanPairing = async (
  code: string,
  myDeviceId: string,
  myDeviceName: string,
  myPairingKey: string,
  pcLocalIp?: string
): Promise<{ success: boolean; device?: LocalPairedDevice; pcUrl?: string }> => {
  // Import subnet scanning helpers
  const { discoverPcOnLan } = require('../utils/lanDiscovery');
  const { scanSubnetForPc } = require('../utils/networkHelpers');

  syncLog('LAN-PAIR', `Attempting LAN pairing with code: ${code}`);

  // Build list of candidate URLs to try
  const candidates: string[] = [];

  // Try stored paired device IPs first
  const pairedDevices = await getLocalPairedDevices();
  for (const device of Object.values(pairedDevices)) {
    for (const ip of device.lastKnownIps) {
      candidates.push(`http://${ip}`);
    }
  }

  // Try subnet discovery
  if (pcLocalIp) {
    const discovered = await discoverPcOnLan(pcLocalIp);
    if (discovered) {
      candidates.push(discovered.url);
    }
  }

  // Also try basic subnet scan
  if (pcLocalIp) {
    const scanned = await scanSubnetForPc(pcLocalIp.split(',')[0]?.trim());
    for (const url of scanned) {
      if (!candidates.includes(url)) candidates.push(url);
    }
  }

  // Deduplicate
  const uniqueCandidates = [...new Set(candidates)];
  syncLog('LAN-PAIR', `Probing ${uniqueCandidates.length} LAN candidate(s) for pairing code match...`);

  // Try each candidate
  for (const baseUrl of uniqueCandidates) {
    try {
      const verifyRes = await fetchWithTimeout(
        `${baseUrl}/api/pair_verify?code=${encodeURIComponent(code)}`,
        { method: 'GET' },
        3000
      );
      if (!verifyRes.ok) continue;
      const verifyData = await verifyRes.json();
      if (!verifyData.match) continue;

      syncLog('LAN-PAIR', `\u2705 Code match found at ${baseUrl} — completing pairing...`);

      // Complete the pairing
      const completeRes = await fetchWithTimeout(
        `${baseUrl}/api/pair_complete`,
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            code,
            deviceId: myDeviceId,
            deviceName: myDeviceName,
            deviceType: 'Android',
            pairingKey: myPairingKey,
          }),
        },
        5000
      );

      if (!completeRes.ok) continue;
      const completeData = await completeRes.json();

      if (completeData.success) {
        // Extract IP from baseUrl
        const urlObj = new URL(baseUrl);
        const ipPort = `${urlObj.hostname}:${urlObj.port || '8999'}`;

        const pairedDevice: LocalPairedDevice = {
          deviceId: verifyData.deviceId || completeData.deviceId,
          deviceName: verifyData.deviceName || completeData.deviceName,
          deviceType: verifyData.deviceType || 'PC',
          pairingKey: verifyData.pairingKey || completeData.pairingKey,
          lastKnownIps: [ipPort],
          cloudflareUrl: verifyData.globalUrl || completeData.globalUrl || '',
          lastSeen: NetworkClock.now(),
          pin: verifyData.pin,
        };

        await saveLocalPairedDevice(pairedDevice);
        syncLog('LAN-PAIR', `\u2705 LAN pairing complete with ${pairedDevice.deviceName}`);

        return { success: true, device: pairedDevice, pcUrl: baseUrl };
      }
    } catch (e: any) {
      syncLog('LAN-PAIR', `Probe failed for ${baseUrl}: ${e?.message || e}`);
    }
  }

  syncLog('LAN-PAIR', 'No PC found with matching code on LAN');
  return { success: false };
};

// ═══ Presence Hook ═══

export function useLanPresence(params: {
  isEnabled: boolean;
  deviceId: string;
  deviceName: string;
  pairingKeyRef: React.MutableRefObject<string>;
  cachedPcUrlRef: React.MutableRefObject<string | null>;
  lastWorkingPcUrlRef: React.MutableRefObject<string | null>;
}) {
  const {
    isEnabled,
    deviceId,
    deviceName,
    pairingKeyRef,
    cachedPcUrlRef,
    lastWorkingPcUrlRef,
  } = params;

  const announceCountRef = useRef(0);
  const isMountedRef = useRef(false);

  useEffect(() => {
    if (!isEnabled || !deviceId) return;

    // M15: Track mount state to prevent timer callbacks after unmount
    isMountedRef.current = true;

    const announcePresence = async () => {
      if (!isMountedRef.current) return;
      // Announce to current connected PC
      const pcUrl = cachedPcUrlRef.current || lastWorkingPcUrlRef.current;
      if (!pcUrl) return;

      try {
        await fetchWithTimeout(
          `${pcUrl}/api/peer_announce`,
          {
            method: 'POST',
            headers: {
              'Content-Type': 'application/json',
              'X-Pairing-Key': pairingKeyRef.current || '',
              'X-FlyShelf-Client': 'MobileCompanion',
            },
            body: JSON.stringify({
              deviceId,
              deviceName,
              deviceType: 'Android',
              pairingKey: pairingKeyRef.current || '',
            }),
          },
          5000
        );
        announceCountRef.current++;
        if (announceCountRef.current % 4 === 0) {
          syncLog('LAN-PRESENCE', `Announced to PC (${announceCountRef.current} total)`);
        }
      } catch {}

      // Also update stored IP for the connected PC
      if (pcUrl) {
        try {
          const devices = await getLocalPairedDevices();
          for (const dev of Object.values(devices)) {
            if (dev.deviceType === 'PC') {
              const urlObj = new URL(pcUrl);
              await updatePairedDeviceIp(dev.deviceId, `${urlObj.hostname}:${urlObj.port || '8999'}`);
            }
          }
        } catch {}
      }
    };

    const timer = setInterval(announcePresence, PRESENCE_INTERVAL);
    // Announce immediately on mount
    announcePresence();

    // Re-announce on network change
    const unsubscribe = NetInfo.addEventListener(state => {
      if (state.isConnected && state.type === 'wifi' && isMountedRef.current) {
        setTimeout(() => { if (isMountedRef.current) announcePresence(); }, 2000);
      }
    });

    return () => {
      isMountedRef.current = false; // M15: Prevent callbacks from firing after cleanup
      clearInterval(timer);
      unsubscribe();
    };
  }, [isEnabled, deviceId, deviceName]);
}
