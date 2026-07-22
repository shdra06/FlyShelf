import { useCallback, useRef, useEffect } from 'react';
import { Platform, Alert, ToastAndroid, NativeModules } from 'react-native';
import AsyncStorage from '@react-native-async-storage/async-storage';
import * as Crypto from 'expo-crypto';
import * as Clipboard from 'expo-clipboard';
import * as Linking from 'expo-linking';
import { CameraView } from 'expo-camera';
import { syncLog } from '../../utils/debugLog';
import { fetchWithTimeout, isValidPairingKey } from '../../utils/networkHelpers';
import { getSecureItem, setSecureItem } from '../../utils/secureStorage';
import { NetworkClock } from '../../utils/networkClock';
import { auth, ensureFirebaseAuth, getFirebaseIdToken, firebaseDatabaseUrl } from '../../firebaseConfig';

const { AdvanceOverlay } = NativeModules;

/**
 * Extracted from index.tsx SyncScreen (lines 1412-1712).
 *
 * Fixes:
 *   C2 — Reduces index.tsx by ~300 lines
 *
 * This hook handles:
 *   1. QR code pairing flow (executePairing)
 *   2. Code-based pairing (connectByCode)
 *   3. Pairing code generation (generateMyPairingCode)
 *   4. QR barcode scan handling
 *   5. Connection polling for incoming pair requests
 */

function createTimeoutSignal(ms: number): AbortSignal {
  const controller = new AbortController();
  const timerId = setTimeout(() => controller.abort(), ms);
  (controller.signal as any)._clearTimeout = () => clearTimeout(timerId);
  return controller.signal;
}

interface UsePairingFlowParams {
  deviceName: string;
  isGlobalSyncEnabled: boolean;
  setGlobalSyncEnabled: (v: boolean) => void;
  pairingKeyRef: React.MutableRefObject<string>;
  cachedPcUrlRef: React.MutableRefObject<string | null>;
  cachedPcUrlTimestampRef: React.MutableRefObject<number>;
  pairingTimestampRef: React.MutableRefObject<number>;
  addPairedDevice: (device: any) => Promise<void>;
  regeneratePairingKey: () => Promise<string>;
  pairedDevices: any[];
  // UI state from external hooks (usePairing + useModals)
  isPairing: boolean;
  setIsPairing: (v: boolean) => void;
  pairedPcName: string | null;
  setPairedPcName: (v: string | null) => void;
  myPairingCode: string | null;
  setMyPairingCode: (v: string | null) => void;
  pairingCodeInput: string;
  setPairingCodeInput: (v: string) => void;
  isQRScannerActive: boolean;
  setIsQRScannerActive: (v: boolean) => void;
  isConnectModalVisible: boolean;
  setIsConnectModalVisible: (v: boolean) => void;
}

interface PairInfo {
  key?: string;
  local?: string;
  global?: string;
  pin?: string;
  name?: string;
  id?: string;
  deviceType?: string;
}

export function usePairingFlow(params: UsePairingFlowParams) {
  const {
    deviceName,
    isGlobalSyncEnabled,
    setGlobalSyncEnabled,
    pairingKeyRef,
    cachedPcUrlRef,
    cachedPcUrlTimestampRef,
    pairingTimestampRef,
    addPairedDevice,
    regeneratePairingKey,
    pairedDevices,
    isPairing,
    setIsPairing,
    pairedPcName,
    setPairedPcName,
    myPairingCode,
    setMyPairingCode,
    pairingCodeInput,
    setPairingCodeInput,
    isQRScannerActive,
    setIsQRScannerActive,
    isConnectModalVisible,
    setIsConnectModalVisible,
  } = params;

  const connectionPollRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const connectionTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  // A-12 fix: ref for isGlobalSyncEnabled to avoid stale closure in pairing poll
  const isGlobalSyncEnabledRef = useRef(isGlobalSyncEnabled);
  useEffect(() => { isGlobalSyncEnabledRef.current = isGlobalSyncEnabled; }, [isGlobalSyncEnabled]);
  const qrProcessingRef = useRef(false);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      if (connectionPollRef.current) clearInterval(connectionPollRef.current);
      if (connectionTimeoutRef.current) clearTimeout(connectionTimeoutRef.current);
    };
  }, []);

  // ─── Execute pairing ───
  const executePairing = useCallback(async (pairInfo: PairInfo) => {
    const { key, local, global: globalUrl, pin, name: pcName, id: pcId } = pairInfo;
    setIsPairing(true);
    if (Platform.OS === 'android') ToastAndroid.show(`Connecting to ${pcName || 'device'}...`, ToastAndroid.SHORT);

    const urls = [local, globalUrl].filter(u => u && u.startsWith('http')) as string[];
    let paired = false, workingUrl = '';
    let pairedPcIsPro = false;
    let pairedPcLicenseKey = '';

    for (const url of urls) {
      try {
        const res = await fetchWithTimeout(`${url}/api/pair`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'X-FlyShelf-Client': 'MobileCompanion' },
          body: JSON.stringify({
            key: key || '',
            deviceId: `Mobile_${(deviceName || 'Phone').replace(/[^a-zA-Z0-9_]/g, '_')}`,
            deviceName: deviceName || 'Phone',
            deviceType: 'Mobile',
          }),
        }, 6000);
        if (res.ok) {
          try {
            const data = await res.json();
            pairedPcIsPro = !!data.isPro;
            pairedPcLicenseKey = data.licenseKey || '';
          } catch (e) {
            console.warn('Pairing response parse: error', (e as any)?.message || e);
          }
          paired = true;
          workingUrl = url;
          break;
        }
      } catch (e) {
        console.warn('Pairing fetch: error', (e as any)?.message || e);
      }
    }

    const pairingTs = NetworkClock.now().toString();
    await Promise.all([
      setSecureItem('pairingKey', key || ''),
      setSecureItem('pairedPcName', pcName || ''),
      setSecureItem('pairedLocalUrl', local || ''),
      setSecureItem('pairedGlobalUrl', globalUrl || ''),
      AsyncStorage.multiSet([
        ['pairedPcId', pcId || ''],
        ['pairedPin', pin || ''],
        ['pairingTimestamp', pairingTs],
      ]),
    ]);
    pairingKeyRef.current = key || '';
    if (Platform.OS === 'android' && AdvanceOverlay?.setPairingKey && key) AdvanceOverlay.setPairingKey(key);
    pairingTimestampRef.current = parseInt(pairingTs);
    if (workingUrl) {
      cachedPcUrlRef.current = workingUrl;
      cachedPcUrlTimestampRef.current = NetworkClock.now();
    }
    setPairedPcName(pcName || 'Device');
    if (!isGlobalSyncEnabled) setGlobalSyncEnabled(true);

    const deviceType = (pairInfo as any).deviceType || 'PC';
    await addPairedDevice({
      deviceId: pcId || `${pcName}_${NetworkClock.now()}`,
      deviceName: pcName || 'Unknown Device',
      deviceType: deviceType as 'PC' | 'Mobile' | 'Browser',
      pairedAt: NetworkClock.now(),
      isPro: pairedPcIsPro,
      licenseKey: pairedPcLicenseKey,
    });

    setIsPairing(false);

    if (paired) {
      if (Platform.OS === 'android') ToastAndroid.show(`✅ Paired with ${pcName}!`, ToastAndroid.LONG);
      Alert.alert('Connected! 🎉',
        `Paired with ${pcName}.\n\nAnything you copy or drop on your PC will appear here instantly — from anywhere in the world.`,
        [{ text: 'Got it!' }]
      );
    } else {
      if (Platform.OS === 'android') ToastAndroid.show(`✅ Paired with ${pcName} (deferred)`, ToastAndroid.LONG);
      Alert.alert('Paired! 🔑',
        `Paired with ${pcName}.\n\nThe PC isn't reachable right now, but your pairing key is saved.\nClipboard sync will start automatically once FlyShelf is running.`,
        [{ text: 'OK' }]
      );
    }
  }, [deviceName, isGlobalSyncEnabled, setGlobalSyncEnabled, pairingKeyRef, cachedPcUrlRef, cachedPcUrlTimestampRef, pairingTimestampRef, addPairedDevice]);

  // ─── Connect by 6-char code ───
  const connectByCode = useCallback(async (code: string) => {
    if (!code || code.trim().length !== 6) {
      Alert.alert('Invalid Code', 'Please enter a 6-character pairing code.');
      return;
    }
    setIsPairing(true);
    if (Platform.OS === 'android') ToastAndroid.show('Looking up code...', ToastAndroid.SHORT);
    try {
      await ensureFirebaseAuth();
      const _authToken = await getFirebaseIdToken();
      const lookupUrl = `${firebaseDatabaseUrl}/pairing_codes/${code.toUpperCase().trim()}.json${_authToken ? `?auth=${_authToken}` : ''}`;
      // A-15 fix: clear timeout signal after fetch completes to prevent timer leak
      const timeoutSignal = createTimeoutSignal(10000);
      let res;
      try {
        res = await fetch(lookupUrl, { signal: timeoutSignal });
      } finally {
        if ((timeoutSignal as any)?._clearTimeout) (timeoutSignal as any)._clearTimeout();
      }
      const data = await res.json();
      if (!data) {
        setIsPairing(false);
        Alert.alert('Code Not Found', 'No device found with this code.\nMake sure the code is correct and the other device is online.');
        return;
      }
      if (data.timestamp && Math.abs(NetworkClock.now() - data.timestamp) > 15 * 60 * 1000) {
        setIsPairing(false);
        Alert.alert('Code Expired', 'This code has expired. Generate a new one on the other device.');
        return;
      }
      await executePairing({
        key: data.pairingKey, local: data.localUrl, global: data.globalUrl,
        pin: data.pin, name: data.deviceName, id: data.deviceId,
      });
      setIsConnectModalVisible(false);
      setPairingCodeInput('');
    } catch (err: any) {
      setIsPairing(false);
      const msg = err?.message || String(err);
      if (msg.includes('timeout') || msg.includes('AbortError')) {
        Alert.alert('Timeout', 'The request timed out. Make sure you have an active internet connection and try again.');
      } else if (msg.toLowerCase().includes('network') || msg.toLowerCase().includes('fetch')) {
        Alert.alert('Network Error', 'Could not reach the pairing server.\n\n• Check your internet connection\n• If on emulator, ensure network is enabled\n\nDetails: ' + msg);
      } else {
        Alert.alert('Error', 'Could not connect.\n\nDetails: ' + msg);
      }
    }
  }, [executePairing]);

  // ─── Generate pairing code ───
  const generateMyPairingCode = useCallback(async () => {
    const chars = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
    let code = '';
    const randomBytes = Crypto.getRandomBytes(6);
    for (let i = 0; i < 6; i++) code += chars[randomBytes[i] % chars.length];
    try {
      await ensureFirebaseAuth();
      const myDeviceId = `Mobile_${(deviceName || 'Phone').replace(/[^a-zA-Z0-9_]/g, '_')}`;
      let currentKey = pairingKeyRef.current;
      if (!currentKey) {
        currentKey = await regeneratePairingKey();
        pairingKeyRef.current = currentKey;
      }
      const payload = {
        deviceId: myDeviceId,
        deviceName: deviceName || 'Phone',
        deviceType: 'Mobile',
        pairingKey: currentKey,
        localUrl: '',
        globalUrl: '',
        pin: '',
        uid: auth.currentUser?.uid || '',
        timestamp: { '.sv': 'timestamp' },
      };
      const _pubToken = await getFirebaseIdToken();
      const writeUrl = `${firebaseDatabaseUrl}/pairing_codes/${code}.json${_pubToken ? `?auth=${_pubToken}` : ''}`;
      const writeRes = await fetch(writeUrl, {
        method: 'PUT',
        signal: createTimeoutSignal(10000),
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });
      if (!writeRes.ok) {
        const errBody = await writeRes.text().catch(() => '');
        Alert.alert('Pairing Error', `Could not publish your code to the cloud (HTTP ${writeRes.status}).`);
        return;
      }
      const verifyRes = await fetch(`${firebaseDatabaseUrl}/pairing_codes/${code}.json${_pubToken ? `?auth=${_pubToken}` : ''}`, { signal: createTimeoutSignal(8000) });
      const verifyData = await verifyRes.json();
      if (!verifyData || !verifyData.pairingKey) {
        Alert.alert('Pairing Error', 'Code was written but could not be verified. Please try again.');
        return;
      }
      setMyPairingCode(code);
      if (Platform.OS === 'android') ToastAndroid.show(`Code: ${code} (5 min) — Waiting for device...`, ToastAndroid.SHORT);

      if (connectionPollRef.current) clearInterval(connectionPollRef.current);
      if (connectionTimeoutRef.current) clearTimeout(connectionTimeoutRef.current);

      // Poll for incoming connections
      const pollForConnection = setInterval(async () => {
        try {
          const _pollToken = await getFirebaseIdToken();
          const codeRes = await fetch(
            `${firebaseDatabaseUrl}/pairing_codes/${code}.json${_pollToken ? `?auth=${_pollToken}` : ''}`,
            { signal: createTimeoutSignal(10000) }
          );
          const codeData = await codeRes.json();
          if (!codeData || !codeData.response) return;
          const resp = codeData.response;
          if (!resp.deviceId || !resp.deviceName || !resp.pairingKey) return;

          const alreadyPaired = (await AsyncStorage.getItem('@pairedDevices') || '[]');
          let pairedList: any[] = [];
          try { pairedList = JSON.parse(alreadyPaired); } catch { pairedList = []; }
          if (pairedList.some((d: any) => d.deviceId === resp.deviceId)) {
            clearInterval(pollForConnection);
            connectionPollRef.current = null;
            if (connectionTimeoutRef.current) { clearTimeout(connectionTimeoutRef.current); connectionTimeoutRef.current = null; }
            setMyPairingCode(null);
            return;
          }

          await addPairedDevice({
            deviceId: resp.deviceId,
            deviceName: resp.deviceName,
            deviceType: resp.deviceType || 'PC',
            pairedAt: NetworkClock.now(),
            isPro: false,
            licenseKey: '',
          });

          if (resp.localUrl) await setSecureItem('pairedLocalUrl', resp.localUrl.startsWith('http') ? resp.localUrl : `http://${resp.localUrl}`);
          if (resp.globalUrl) await setSecureItem('pairedGlobalUrl', resp.globalUrl);
          if (resp.pairingKey && resp.pairingKey !== pairingKeyRef.current) {
            pairingKeyRef.current = resp.pairingKey;
          }

          setPairedPcName(resp.deviceName);
          if (!isGlobalSyncEnabledRef.current) setGlobalSyncEnabled(true);
          if (Platform.OS === 'android') ToastAndroid.show(`✅ Paired with ${resp.deviceName}!`, ToastAndroid.LONG);

          clearInterval(pollForConnection);
          connectionPollRef.current = null;
          if (connectionTimeoutRef.current) { clearTimeout(connectionTimeoutRef.current); connectionTimeoutRef.current = null; }
          setMyPairingCode(null);
          try {
            const _delToken = await getFirebaseIdToken();
            await fetch(`${firebaseDatabaseUrl}/pairing_codes/${code}.json${_delToken ? `?auth=${_delToken}` : ''}`, { method: 'DELETE', signal: createTimeoutSignal(10000) });
          } catch {}
        } catch (e) {
          syncLog('PAIR', `Connection poll error: ${(e as any)?.message || e}`);
        }
      }, 3000);
      connectionPollRef.current = pollForConnection;

      // Auto-expire after 5 min
      connectionTimeoutRef.current = setTimeout(async () => {
        clearInterval(pollForConnection);
        connectionPollRef.current = null;
        connectionTimeoutRef.current = null;
        try {
          const _expToken = await getFirebaseIdToken();
          await fetch(`${firebaseDatabaseUrl}/pairing_codes/${code}.json${_expToken ? `?auth=${_expToken}` : ''}`, { method: 'DELETE', signal: createTimeoutSignal(10000) });
        } catch {}
        setMyPairingCode(null);
      }, 5 * 60 * 1000);
    } catch (error: any) {
      Alert.alert('Error', 'Could not generate code.\n\nDetails: ' + (error?.message || String(error)));
    }
  }, [deviceName, pairingKeyRef, regeneratePairingKey, addPairedDevice, setGlobalSyncEnabled]);

  // ─── QR barcode scan handler ───
  const handleBarcodeScanned = useCallback(async ({ data }: { data: string }) => {
    if (qrProcessingRef.current) return;
    qrProcessingRef.current = true;
    setIsQRScannerActive(false);
    try {
      let qr: any = null;
      try { qr = JSON.parse(data); } catch {}
      if (qr && qr.app === 'FlyShelf') {
        await executePairing({ key: qr.key, local: qr.local, global: qr.global, pin: qr.pin, name: qr.name, id: qr.id });
        return;
      }
      await Clipboard.setStringAsync(data);
      if (Platform.OS === 'android') ToastAndroid.show('Copied QR content', ToastAndroid.SHORT);
      if (data.toLowerCase().startsWith('http://') || data.toLowerCase().startsWith('https://')) Linking.openURL(data).catch(() => {});
    } catch (e: any) {
      syncLog('QR', `Scan handling failed: ${e?.message || e}`);
      Alert.alert('QR Error', e?.message || 'Failed to process QR code.');
    } finally {
      qrProcessingRef.current = false;
    }
  }, [executePairing]);

  return {
    executePairing,
    connectByCode,
    generateMyPairingCode,
    handleBarcodeScanned,
  };
}
