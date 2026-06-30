import React, { createContext, useState, useEffect, useContext, useCallback } from 'react';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { getSecureItem, setSecureItem } from '../utils/secureStorage';
import * as Crypto from 'expo-crypto';

export type ConnectionType = 'LAN' | 'Cloud' | 'Offline';

export type PairedDevice = {
  deviceId: string;
  deviceName: string;
  deviceType: 'PC' | 'Mobile' | 'Browser';
  pairedAt: number; // timestamp
  isPro?: boolean;
  licenseKey?: string;
  // Live status fields (updated by sync loop, not persisted)
  lastSeen?: number;
  isOnline?: boolean;
  connectionType?: ConnectionType;
  latencyMs?: number;
  localUrl?: string;
  globalUrl?: string;
};

type SettingsContextType = {
  pcLocalIp: string;
  setPcLocalIp: (ip: string) => Promise<void>;
  deviceName: string;
  setDeviceName: (name: string) => Promise<void>;
  deviceId: string;
  isGlobalSyncEnabled: boolean;
  setGlobalSyncEnabled: (val: boolean) => Promise<void>;
  isFloatingBallEnabled: boolean;
  setFloatingBallEnabled: (val: boolean) => Promise<void>;
  defaultTargetDeviceName: string;
  setDefaultTargetDeviceName: (name: string) => Promise<void>;
  floatingBallSize: number;
  setFloatingBallSize: (val: number) => Promise<void>;
  floatingBallAutoHide: number;
  setFloatingBallAutoHide: (val: number) => Promise<void>;
  // ── Paired Devices ──
  pairedDevices: PairedDevice[];
  addPairedDevice: (device: PairedDevice) => Promise<void>;
  removePairedDevice: (deviceId: string) => Promise<void>;
  updatePairedDeviceLicensing: (deviceId: string, isPro: boolean, licenseKey: string) => Promise<void>;
  updateDeviceStatus: (deviceId: string, status: { isOnline?: boolean; connectionType?: ConnectionType; latencyMs?: number; lastSeen?: number; localUrl?: string; globalUrl?: string }) => void;
  pairingKey: string;
  regeneratePairingKey: () => Promise<string>;
};

const SettingsContext = createContext<SettingsContextType>({
  pcLocalIp: '',
  setPcLocalIp: async () => {},
  deviceName: '',
  setDeviceName: async () => {},
  deviceId: '',
  isGlobalSyncEnabled: true,
  setGlobalSyncEnabled: async () => {},
  isFloatingBallEnabled: false,
  setFloatingBallEnabled: async () => {},
  defaultTargetDeviceName: '',
  setDefaultTargetDeviceName: async () => {},
  floatingBallSize: 48,
  setFloatingBallSize: async () => {},
  floatingBallAutoHide: 3000,
  setFloatingBallAutoHide: async () => {},
  pairedDevices: [],
  addPairedDevice: async () => {},
  removePairedDevice: async () => {},
  updatePairedDeviceLicensing: async () => {},
  updateDeviceStatus: () => {},
  pairingKey: '',
  regeneratePairingKey: async () => '',
});

export const useSettings = () => useContext(SettingsContext);

/** Generate a 32-char hex key (same format as PC's Guid.ToString("N")) using CSPRNG */
const generatePairingKey = (): string => {
  const randomBytes = Crypto.getRandomBytes(16);
  return Array.from(randomBytes)
    .map(b => b.toString(16).padStart(2, '0'))
    .join('');
};

export const SettingsProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const [pcLocalIp, setPcLocalIpState] = useState('');
  const [deviceName, setDeviceNameState] = useState('');
  const [deviceId, setDeviceIdState] = useState('');
  const [isGlobalSyncEnabled, setGlobalSyncEnabledState] = useState(true);
  const [isFloatingBallEnabled, setFloatingBallEnabledState] = useState(false);
  const [defaultTargetDeviceName, setDefaultTargetDeviceNameState] = useState('');
  const [floatingBallSize, setFloatingBallSizeState] = useState(48);
  const [floatingBallAutoHide, setFloatingBallAutoHideState] = useState(3000);
  const [pairedDevices, setPairedDevicesState] = useState<PairedDevice[]>([]);
  const [pairingKey, setPairingKeyState] = useState('');

  useEffect(() => {
    const initStorage = async () => {
      const keys = [
        '@pcLocalIp', '@deviceName', '@isCloudDiscoveryEnabled', '@isGlobalSyncEnabled',
        '@isFloatingBallEnabled', '@defaultTargetDeviceName', '@floatingBallSize',
        '@floatingBallAutoHide', '@deviceId', '@pairedDevices'
      ];
      const results = await AsyncStorage.multiGet(keys);
      const values = Object.fromEntries(results);

      if (values['@pcLocalIp']) setPcLocalIpState(values['@pcLocalIp']!);
      if (values['@deviceName']) setDeviceNameState(values['@deviceName']!);

      const globalSync = values['@isCloudDiscoveryEnabled'] ?? values['@isGlobalSyncEnabled'];
      if (globalSync !== null && globalSync !== undefined) setGlobalSyncEnabledState(globalSync === 'true');

      if (values['@isFloatingBallEnabled'] !== null && values['@isFloatingBallEnabled'] !== undefined)
        setFloatingBallEnabledState(values['@isFloatingBallEnabled'] === 'true');
      if (values['@defaultTargetDeviceName']) setDefaultTargetDeviceNameState(values['@defaultTargetDeviceName']!);
      if (values['@floatingBallSize']) setFloatingBallSizeState(parseInt(values['@floatingBallSize']!, 10));
      if (values['@floatingBallAutoHide']) setFloatingBallAutoHideState(parseInt(values['@floatingBallAutoHide']!, 10));

      let storedId = values['@deviceId'];
      if (!storedId) {
        storedId = 'MOB-' + Date.now().toString(36) + Math.random().toString(36).substring(2, 7);
        await AsyncStorage.setItem('@deviceId', storedId);
      }
      setDeviceIdState(storedId);

      // ── Paired Devices ──
      const storedDevices = values['@pairedDevices'];
      if (storedDevices) {
        try { setPairedDevicesState(JSON.parse(storedDevices)); } catch {}
      }

      // ── Pairing Key (also stored as 'pairingKey' for backward compat with index.tsx) ──
      // Pairing key from secure storage (can't use multiGet)
      let storedKey = await getSecureItem('pairingKey');
      if (storedKey) {
        setPairingKeyState(storedKey);
      }
      // Note: pairingKey may remain '' until user pairs via QR/code — that's intentional
    };
    initStorage();
  }, []);

  const setPcLocalIp = useCallback(async (ip: string) => {
    setPcLocalIpState(ip);
    await AsyncStorage.setItem('@pcLocalIp', ip);
  }, []);

  const setDeviceName = useCallback(async (name: string) => {
    setDeviceNameState(name);
    await AsyncStorage.setItem('@deviceName', name);
  }, []);

  const setGlobalSyncEnabled = useCallback(async (val: boolean) => {
    setGlobalSyncEnabledState(val);
    await AsyncStorage.setItem('@isCloudDiscoveryEnabled', val.toString());
  }, []);

  const setFloatingBallEnabled = useCallback(async (val: boolean) => {
    setFloatingBallEnabledState(val);
    await AsyncStorage.setItem('@isFloatingBallEnabled', val.toString());
  }, []);

  const setDefaultTargetDeviceName = useCallback(async (name: string) => {
    setDefaultTargetDeviceNameState(name);
    await AsyncStorage.setItem('@defaultTargetDeviceName', name);
  }, []);

  const setFloatingBallSize = useCallback(async (val: number) => {
    setFloatingBallSizeState(val);
    await AsyncStorage.setItem('@floatingBallSize', val.toString());
  }, []);

  const setFloatingBallAutoHide = useCallback(async (val: number) => {
    setFloatingBallAutoHideState(val);
    await AsyncStorage.setItem('@floatingBallAutoHide', val.toString());
  }, []);

  // ── Paired Devices ──
  const addPairedDevice = useCallback(async (device: PairedDevice) => {
    setPairedDevicesState(prev => {
      // Dedup: update if already exists, otherwise add (max 5)
      const existing = prev.findIndex(d => d.deviceId === device.deviceId);
      let updated: PairedDevice[];
      if (existing >= 0) {
        updated = [...prev];
        updated[existing] = { ...device, pairedAt: prev[existing].pairedAt }; // keep original pairedAt
      } else {
        updated = [...prev, device];
        if (updated.length > 5) updated = updated.slice(-5); // keep latest 5
      }
      AsyncStorage.setItem('@pairedDevices', JSON.stringify(updated)).catch(() => {});
      return updated;
    });
  }, []);

  const removePairedDevice = useCallback(async (deviceId: string) => {
    setPairedDevicesState(prev => {
      const updated = prev.filter(d => d.deviceId !== deviceId);
      AsyncStorage.setItem('@pairedDevices', JSON.stringify(updated)).catch(() => {});
      // If no devices remain, clear all legacy pairing state so the home screen
      // stops showing "Paired with ..." for a device that was removed.
      if (updated.length === 0) {
        AsyncStorage.multiRemove([
          'pairedPcName', 'pairedPcId', 'pairedLocalUrl', 'pairedGlobalUrl', 'pairedPin',
        ]).catch(() => {});
      }
      return updated;
    });
  }, []);

  const updatePairedDeviceLicensing = useCallback(async (deviceId: string, isPro: boolean, licenseKey: string) => {
    setPairedDevicesState(prev => {
      const idx = prev.findIndex(d => d.deviceId === deviceId);
      if (idx === -1) return prev;
      if (prev[idx].isPro === isPro && prev[idx].licenseKey === licenseKey) return prev;
      
      const updated = [...prev];
      updated[idx] = {
        ...updated[idx],
        isPro,
        licenseKey
      };
      AsyncStorage.setItem('@pairedDevices', JSON.stringify(updated)).catch(() => {});
      return updated;
    });
  }, []);

  /** Update live connection status for a device (not persisted — runtime only) */
  const updateDeviceStatus = useCallback((deviceId: string, status: { isOnline?: boolean; connectionType?: ConnectionType; latencyMs?: number; lastSeen?: number; localUrl?: string; globalUrl?: string }) => {
    setPairedDevicesState(prev => {
      const idx = prev.findIndex(d => d.deviceId === deviceId);
      if (idx === -1) return prev;
      const device = prev[idx];
      // Only update if something actually changed
      const changed = Object.entries(status).some(([k, v]) => (device as any)[k] !== v);
      if (!changed) return prev;
      const updated = [...prev];
      updated[idx] = { ...device, ...status };
      // Don't persist live status to AsyncStorage — it's ephemeral
      return updated;
    });
  }, []);

  const regeneratePairingKey = useCallback(async (): Promise<string> => {
    const newKey = generatePairingKey();
    setPairingKeyState(newKey);
    await setSecureItem('pairingKey', newKey);
    return newKey;
  }, []);

  return (
    <SettingsContext.Provider value={{ pcLocalIp, setPcLocalIp, deviceName, setDeviceName, deviceId, isGlobalSyncEnabled, setGlobalSyncEnabled, isFloatingBallEnabled, setFloatingBallEnabled, defaultTargetDeviceName, setDefaultTargetDeviceName, floatingBallSize, setFloatingBallSize, floatingBallAutoHide, setFloatingBallAutoHide, pairedDevices, addPairedDevice, removePairedDevice, updatePairedDeviceLicensing, updateDeviceStatus, pairingKey, regeneratePairingKey }}>
      {children}
    </SettingsContext.Provider>
  );
};
