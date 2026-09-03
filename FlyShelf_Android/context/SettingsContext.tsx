import React, { createContext, useState, useEffect, useContext, useCallback, useRef, useMemo } from 'react';
import AsyncStorage from '@react-native-async-storage/async-storage';
import EncryptedStorage from '../utils/EncryptedStorage';
import { getSecureItem, setSecureItem } from '../utils/secureStorage';
import { clearKeyCache } from '../utils/syncCrypto';
import * as Crypto from 'expo-crypto';

export type ConnectionType = 'LAN' | 'Cloud' | 'Offline';

export type DeviceSyncPrefs = {
  clipboard: boolean;
  images: boolean;
  files: boolean;
  notes: boolean;
  todos: boolean;
};

const DEFAULT_SYNC_PREFS: DeviceSyncPrefs = { clipboard: true, images: true, files: true, notes: true, todos: true };

export type SyncPreferences = Record<string, DeviceSyncPrefs>;

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
  isLoading: boolean;
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
  // ── Auto Sync Mode ──
  autoSyncTop5: boolean;
  setAutoSyncTop5: (val: boolean) => Promise<void>;
  // ── Offline Outbox Queue (Pro Feature — currently free) ──
  isOfflineOutboxEnabled: boolean;
  setIsOfflineOutboxEnabled: (val: boolean) => Promise<void>;
  // ── FCM Silent Wake ──
  isFcmSilentWakeEnabled: boolean;
  setIsFcmSilentWakeEnabled: (val: boolean) => Promise<void>;
  // ── Per-Device Sync Preferences ──
  syncPreferences: SyncPreferences;
  setSyncPreference: (deviceId: string, category: keyof DeviceSyncPrefs, enabled: boolean) => Promise<void>;
  getSyncPrefsForDevice: (deviceId: string) => DeviceSyncPrefs;
  setAllSyncPrefsForDevice: (deviceId: string, prefs: DeviceSyncPrefs) => Promise<void>;
};

const SettingsContext = createContext<SettingsContextType>({
  pcLocalIp: '',
  setPcLocalIp: async () => {},
  deviceName: '',
  setDeviceName: async () => {},
  deviceId: '',
  isLoading: true,
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
  autoSyncTop5: true,
  setAutoSyncTop5: async () => {},
  isOfflineOutboxEnabled: false,
  setIsOfflineOutboxEnabled: async () => {},
  isFcmSilentWakeEnabled: false,
  setIsFcmSilentWakeEnabled: async () => {},
  syncPreferences: {},
  setSyncPreference: async () => {},
  getSyncPrefsForDevice: () => DEFAULT_SYNC_PREFS,
  setAllSyncPrefsForDevice: async () => {},
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
  const [autoSyncTop5, setAutoSyncTop5State] = useState(true);
  const [isOfflineOutboxEnabled, setIsOfflineOutboxEnabledState] = useState(false);
  const [isFcmSilentWakeEnabled, setIsFcmSilentWakeEnabledState] = useState(false);
  const [pairedDevices, setPairedDevicesState] = useState<PairedDevice[]>([]);
  const [pairingKey, setPairingKeyState] = useState('');
  const [syncPreferences, setSyncPreferencesState] = useState<SyncPreferences>({});
  const [isLoading, setIsLoading] = useState(true);
  const pairedDevicesHydrated = useRef(false);

  const setAutoSyncTop5 = useCallback(async (val: boolean) => {
    setAutoSyncTop5State(val);
    await AsyncStorage.setItem('@autoSyncTop5', String(val)).catch(() => {});
  }, []);

  const setIsOfflineOutboxEnabled = useCallback(async (val: boolean) => {
    setIsOfflineOutboxEnabledState(val);
    // PRO FEATURE GATE — uncomment when Pro licensing is enforced:
    // if (!isPro) { setIsOfflineOutboxEnabledState(false); return; }
    await AsyncStorage.setItem('@isOfflineOutboxEnabled', String(val)).catch(() => {});
  }, []);

  const setIsFcmSilentWakeEnabled = useCallback(async (val: boolean) => {
    setIsFcmSilentWakeEnabledState(val);
    await AsyncStorage.setItem('@isFcmSilentWakeEnabled', String(val)).catch(() => {});
  }, []);

  useEffect(() => {
    const initStorage = async () => {
      try {
        const autoSyncStored = await AsyncStorage.getItem('@autoSyncTop5');
        if (autoSyncStored !== null) setAutoSyncTop5State(autoSyncStored === 'true');

        const offlineOutboxStored = await AsyncStorage.getItem('@isOfflineOutboxEnabled');
        if (offlineOutboxStored !== null) setIsOfflineOutboxEnabledState(offlineOutboxStored === 'true');

        const fcmStored = await AsyncStorage.getItem('@isFcmSilentWakeEnabled');
        if (fcmStored !== null) setIsFcmSilentWakeEnabledState(fcmStored === 'true');

        const keys = [
          '@pcLocalIp', '@deviceName', '@isCloudDiscoveryEnabled', '@isGlobalSyncEnabled',
          '@isFloatingBallEnabled', '@defaultTargetDeviceName', '@floatingBallSize',
          '@floatingBallAutoHide', '@deviceId', '@pairedDevices'
        ];
        const results = await EncryptedStorage.multiGet(keys);
        const values = Object.fromEntries(results);

        if (values['@pcLocalIp']) setPcLocalIpState(values['@pcLocalIp']!);
        if (values['@deviceName']) setDeviceNameState(values['@deviceName']!);

        const globalSync = values['@isCloudDiscoveryEnabled'] ?? values['@isGlobalSyncEnabled'];
        if (globalSync !== null && globalSync !== undefined) setGlobalSyncEnabledState(globalSync === 'true');

        if (values['@isFloatingBallEnabled'] !== null && values['@isFloatingBallEnabled'] !== undefined)
          setFloatingBallEnabledState(values['@isFloatingBallEnabled'] === 'true');
        if (values['@defaultTargetDeviceName']) setDefaultTargetDeviceNameState(values['@defaultTargetDeviceName']!);

        if (values['@floatingBallSize']) {
          const parsed = parseInt(values['@floatingBallSize']!, 10);
          if (!isNaN(parsed)) setFloatingBallSizeState(parsed);
        }
        if (values['@floatingBallAutoHide']) {
          const parsed = parseInt(values['@floatingBallAutoHide']!, 10);
          if (!isNaN(parsed)) setFloatingBallAutoHideState(parsed);
        }

        let storedId = values['@deviceId'];
        if (!storedId) {
          storedId = 'MOB-' + Date.now().toString(36) + Math.random().toString(36).substring(2, 12);
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

        // ── Sync Preferences ──
        const storedSyncPrefs = await EncryptedStorage.getItem('@syncPreferences');
        if (storedSyncPrefs) {
          try { setSyncPreferencesState(JSON.parse(storedSyncPrefs)); } catch {}
        }

        pairedDevicesHydrated.current = true;
      } catch (e) {
        console.error('[SettingsContext] initStorage failed:', e);
      } finally {
        setIsLoading(false);
      }
    };
    initStorage();
  }, []);

  // ── M-7: Persist pairedDevices whenever they change (skip initial empty state) ──
  // M-19: Only persist stable identity fields, NOT ephemeral live status (isOnline, latencyMs, etc.)
  const persistableDevicesRef = useRef('');
  useEffect(() => {
    if (!pairedDevicesHydrated.current) return; // skip until hydration is done
    const EPHEMERAL_KEYS = ['isOnline', 'connectionType', 'latencyMs', 'lastSeen', 'localUrl', 'globalUrl'];
    const stableDevices = pairedDevices.map(d => {
      const clean = { ...d } as Record<string, unknown>;
      EPHEMERAL_KEYS.forEach(k => delete clean[k]);
      return clean;
    });
    const json = JSON.stringify(stableDevices);
    if (json === persistableDevicesRef.current) return; // no stable change
    persistableDevicesRef.current = json;
    EncryptedStorage.setItem('@pairedDevices', json).catch(() => {});
    // If no devices remain, clear legacy pairing state
    if (pairedDevices.length === 0) {
      AsyncStorage.multiRemove([
        'pairedPcName', 'pairedPcId', 'pairedLocalUrl', 'pairedGlobalUrl', 'pairedPin',
      ]).catch(() => {});
    }
  }, [pairedDevices]);

  const setPcLocalIp = useCallback(async (ip: string) => {
    setPcLocalIpState(ip);
    await EncryptedStorage.setItem('@pcLocalIp', ip);
  }, []);

  const setDeviceName = useCallback(async (name: string) => {
    setDeviceNameState(name);
    await EncryptedStorage.setItem('@deviceName', name);
  }, []);

  const setGlobalSyncEnabled = useCallback(async (val: boolean) => {
    setGlobalSyncEnabledState(val);
    await AsyncStorage.setItem('@isGlobalSyncEnabled', val.toString());
  }, []);

  const setFloatingBallEnabled = useCallback(async (val: boolean) => {
    setFloatingBallEnabledState(val);
    await AsyncStorage.setItem('@isFloatingBallEnabled', val.toString());
  }, []);

  const setDefaultTargetDeviceName = useCallback(async (name: string) => {
    setDefaultTargetDeviceNameState(name);
    await EncryptedStorage.setItem('@defaultTargetDeviceName', name);
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
      if (existing >= 0) {
        const updated = [...prev];
        updated[existing] = { ...device, pairedAt: prev[existing].pairedAt }; // keep original pairedAt
        return updated;
      }
      let updated = [...prev, device];
      if (updated.length > 5) updated = updated.slice(-5); // keep latest 5
      return updated;
    });
    // Persistence handled by useEffect on pairedDevices
  }, []);

  const removePairedDevice = useCallback(async (deviceId: string) => {
    setPairedDevicesState(prev => prev.filter(d => d.deviceId !== deviceId));
    // Persistence & legacy cleanup handled by useEffect on pairedDevices
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
      // Persistence handled by useEffect on pairedDevices
      return updated;
    });
  }, []);

  /** H-8 FIX: Debounced device status updates to prevent 30+ re-renders/min.
   *  Batches ephemeral status changes (latencyMs, lastSeen) and only commits
   *  to state on significant changes (online/offline) or every 10s.
   */
  const pendingStatusRef = useRef<Map<string, { isOnline?: boolean; connectionType?: ConnectionType; latencyMs?: number; lastSeen?: number; localUrl?: string; globalUrl?: string }>>(new Map());
  const statusFlushTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const flushDeviceStatus = useCallback(() => {
    const pending = pendingStatusRef.current;
    if (pending.size === 0) return;
    setPairedDevicesState(prev => {
      let changed = false;
      const updated = prev.map(device => {
        let status = pending.get(device.deviceId);
        if (!status && device.deviceType === 'PC') {
          // Fallback matching for PC device if key was prefixed or matched name
          for (const [key, val] of pending.entries()) {
            if (key.includes(device.deviceId) || device.deviceId.includes(key) || key.toLowerCase() === device.deviceName?.toLowerCase()) {
              status = val;
              break;
            }
          }
        }
        if (!status) return device;
        const hasChange = Object.entries(status).some(([k, v]) => (device as any)[k] !== v);
        if (!hasChange) return device;
        changed = true;
        return { ...device, ...status };
      });
      return changed ? updated : prev;
    });
    pendingStatusRef.current = new Map();
  }, []);

  const updateDeviceStatus = useCallback((deviceId: string, status: { isOnline?: boolean; connectionType?: ConnectionType; latencyMs?: number; lastSeen?: number; localUrl?: string; globalUrl?: string }) => {
    const existing = pendingStatusRef.current.get(deviceId) || {};
    pendingStatusRef.current.set(deviceId, { ...existing, ...status });

    // Immediately flush on significant state changes (online ↔ offline)
    if (status.isOnline !== undefined) {
      if (statusFlushTimerRef.current) clearTimeout(statusFlushTimerRef.current);
      statusFlushTimerRef.current = null;
      flushDeviceStatus();
      return;
    }
    // Batch minor updates (latency, lastSeen) — flush every 10s
    if (!statusFlushTimerRef.current) {
      statusFlushTimerRef.current = setTimeout(() => {
        statusFlushTimerRef.current = null;
        flushDeviceStatus();
      }, 10000);
    }
  }, [flushDeviceStatus]);

  const regeneratePairingKey = useCallback(async (): Promise<string> => {
    const newKey = generatePairingKey();
    setPairingKeyState(newKey);
    await setSecureItem('pairingKey', newKey);
    // Invalidate cached crypto key so it re-derives from the new pairing key
    clearKeyCache();
    return newKey;
  }, []);

  // ── Per-Device Sync Preferences ──
  const setSyncPreference = useCallback(async (deviceId: string, category: keyof DeviceSyncPrefs, enabled: boolean) => {
    setSyncPreferencesState(prev => {
      const devicePrefs = prev[deviceId] || { ...DEFAULT_SYNC_PREFS };
      const updated = { ...prev, [deviceId]: { ...devicePrefs, [category]: enabled } };
      EncryptedStorage.setItem('@syncPreferences', JSON.stringify(updated)).catch(() => {});
      return updated;
    });
  }, []);

  const getSyncPrefsForDevice = useCallback((deviceId: string): DeviceSyncPrefs => {
    return syncPreferences[deviceId] || { ...DEFAULT_SYNC_PREFS };
  }, [syncPreferences]);

  const setAllSyncPrefsForDevice = useCallback(async (deviceId: string, prefs: DeviceSyncPrefs) => {
    setSyncPreferencesState(prev => {
      const updated = { ...prev, [deviceId]: prefs };
      EncryptedStorage.setItem('@syncPreferences', JSON.stringify(updated)).catch(() => {});
      return updated;
    });
  }, []);

  const providerValue = useMemo(() => ({
    pcLocalIp, setPcLocalIp, deviceName, setDeviceName, deviceId, isLoading,
    isGlobalSyncEnabled, setGlobalSyncEnabled, isFloatingBallEnabled, setFloatingBallEnabled,
    defaultTargetDeviceName, setDefaultTargetDeviceName, floatingBallSize, setFloatingBallSize,
    floatingBallAutoHide, setFloatingBallAutoHide, pairedDevices, addPairedDevice, removePairedDevice,
    updatePairedDeviceLicensing, updateDeviceStatus, pairingKey, regeneratePairingKey,
    autoSyncTop5, setAutoSyncTop5,
    isOfflineOutboxEnabled, setIsOfflineOutboxEnabled,
    isFcmSilentWakeEnabled, setIsFcmSilentWakeEnabled,
    syncPreferences, setSyncPreference, getSyncPrefsForDevice, setAllSyncPrefsForDevice,
  }), [
    pcLocalIp, setPcLocalIp, deviceName, setDeviceName, deviceId, isLoading,
    isGlobalSyncEnabled, setGlobalSyncEnabled, isFloatingBallEnabled, setFloatingBallEnabled,
    defaultTargetDeviceName, setDefaultTargetDeviceName, floatingBallSize, setFloatingBallSize,
    floatingBallAutoHide, setFloatingBallAutoHide, pairedDevices, addPairedDevice, removePairedDevice,
    updatePairedDeviceLicensing, updateDeviceStatus, pairingKey, regeneratePairingKey,
    autoSyncTop5, setAutoSyncTop5,
    isOfflineOutboxEnabled, setIsOfflineOutboxEnabled,
    isFcmSilentWakeEnabled, setIsFcmSilentWakeEnabled,
    syncPreferences, setSyncPreference, getSyncPrefsForDevice, setAllSyncPrefsForDevice,
  ]);

  return (
    <SettingsContext.Provider value={providerValue}>
      {children}
    </SettingsContext.Provider>
  );
};
