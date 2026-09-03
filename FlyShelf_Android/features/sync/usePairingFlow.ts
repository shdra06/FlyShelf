import { useCallback, useRef, useEffect, useState } from 'react';
import { Platform, Alert, ToastAndroid, NativeModules } from 'react-native';
import { toast } from '../../context/ToastContext';
import AsyncStorage from '@react-native-async-storage/async-storage';
import * as Crypto from 'expo-crypto';
import * as Clipboard from 'expo-clipboard';
import * as Linking from 'expo-linking';
import { CameraView } from 'expo-camera';
import { syncLog } from '../../utils/debugLog';
import { fetchWithTimeout, isValidPairingKey } from '../../utils/networkHelpers';
import { getSecureItem, setSecureItem } from '../../utils/secureStorage';
import { NetworkClock } from '../../utils/networkClock';
import { auth, ensureFirebaseAuth, getFirebaseIdToken, firebaseDatabaseUrl, database } from '../../firebaseConfig';
import { ref, set } from 'firebase/database';
import { encrypt, decrypt, clearKeyCache } from '../../utils/syncCrypto';

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

// C-1 FIX: Local createTimeoutSignal that returns both signal and cleanup function
// to prevent timer/AbortController memory leaks during pairing
function createTimeoutSignal(ms: number): AbortSignal {
  const controller = new AbortController();
  const timerId = setTimeout(() => controller.abort(), ms);
  (controller.signal as any)._clearTimeout = () => clearTimeout(timerId);
  return controller.signal;
}

/** C-1 FIX: Helper to clear a timeout signal created by createTimeoutSignal */
function clearSignalTimeout(signal: AbortSignal): void {
  try { (signal as any)?._clearTimeout?.(); } catch {}
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
  const abortControllerRef = useRef<AbortController | null>(null);

  // A-12 fix: ref for isGlobalSyncEnabled to avoid stale closure in pairing poll
  const isGlobalSyncEnabledRef = useRef(isGlobalSyncEnabled);
  useEffect(() => { isGlobalSyncEnabledRef.current = isGlobalSyncEnabled; }, [isGlobalSyncEnabled]);
  const qrProcessingRef = useRef(false);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      if (connectionPollRef.current) clearInterval(connectionPollRef.current);
      if (connectionTimeoutRef.current) clearTimeout(connectionTimeoutRef.current);
      abortControllerRef.current?.abort();
    };
  }, []);

  // ─── Execute pairing ───
  const executePairing = useCallback(async (pairInfo: PairInfo) => {
    const { key, local, global: globalUrl, pin, name: pcName, id: pcId } = pairInfo;
    setIsPairing(true);
    toast.info('Connecting to Device...', `Establishing handshake with ${pcName || 'paired device'}`);

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

    const myDeviceId = `Mobile_${(deviceName || 'Phone').replace(/[^a-zA-Z0-9_]/g, '_')}`;
    const myDeviceName = deviceName || 'Phone';

    // ─── Firebase Cloud Handshake (Instant discovery across networks) ───
    if (key) {
      try {
        await ensureFirebaseAuth();
        const uid = auth?.currentUser?.uid;
        if (uid) {
          await set(ref(database, `members/${key}/${uid}`), true).catch(() => {});
          syncLog('PAIR', `✅ Registered room membership for ${key.substring(0, 8)}...`);
        }

        // Write handshake node so PC's CheckForHandshakes picks it up in <2 seconds
        await set(ref(database, `pairing_handshake/${key}/${myDeviceId}`), {
          deviceId: myDeviceId,
          deviceName: myDeviceName,
          deviceType: 'Mobile',
          timestamp: NetworkClock.now(),
        }).catch(() => {});
        syncLog('PAIR', `✅ Wrote cloud pairing handshake to Firebase`);

        // Write active device node for live room presence
        await set(ref(database, `active_devices/${key}/${myDeviceId}`), {
          DeviceId: myDeviceId,
          DeviceName: myDeviceName,
          DeviceType: 'Mobile',
          IsOnline: true,
          LocalIp: '',
          Timestamp: NetworkClock.now(),
        }).catch(() => {});
      } catch (fbErr: any) {
        syncLog('PAIR', `Firebase handshake error: ${fbErr?.message || fbErr}`);
      }
    }

    const pairingTs = NetworkClock.now().toString();
    clearKeyCache();
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
    if (workingUrl) {
      cachedPcUrlRef.current = workingUrl;
      cachedPcUrlTimestampRef.current = NetworkClock.now();
      await AsyncStorage.setItem('@flyshelf_last_lan_url', workingUrl).catch(() => {});
      await setSecureItem('pairedLocalUrl', workingUrl).catch(() => {});
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
      toast.success(`Paired with ${pcName}!`, 'Connected via LAN');
      Alert.alert('Paired! 🎉',
        `Connected to ${pcName} on your local network.\n\nClipboard, files, and screenshots will sync automatically.`,
        [{ text: 'Got it!' }]
      );
    } else {
      toast.success(`Paired with ${pcName}!`, 'Saved — searching for PC...');
      Alert.alert('Paired! 🎉',
        `Pairing with ${pcName} saved via cloud.\n\nWill connect automatically when your PC is reachable.`,
        [{ text: 'Got it!' }]
      );
    }
  }, [deviceName, isGlobalSyncEnabled, setGlobalSyncEnabled, pairingKeyRef, cachedPcUrlRef, cachedPcUrlTimestampRef, pairingTimestampRef, addPairedDevice, setPairedPcName, setIsPairing]);

  // ─── Connect by 6-char code ───
  const connectByCode = useCallback(async (code: string) => {
    if (!code || code.trim().length !== 6) {
      Alert.alert('Invalid Code', 'Please enter a 6-character pairing code.');
      return;
    }
    setIsPairing(true);
    toast.info('Validating Code...', `Looking up 6-character PIN ${code.toUpperCase()}`);
    syncLog('PAIR', `[STEP 4/6: PAIR JOIN 1/2] Looking up code ${code.toUpperCase()} in Firebase...`);
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
        syncLog('PAIR', `[STEP 4/6: PAIR JOIN ERROR] ❌ Code ${code.toUpperCase()} not found in Firebase`);
        Alert.alert('Code Not Found', 'No device found with this code.\nMake sure the code is correct and the other device is online.');
        return;
      }
      if (data.timestamp && Math.abs(NetworkClock.now() - data.timestamp) > 15 * 60 * 1000) {
        setIsPairing(false);
        syncLog('PAIR', `[STEP 4/6: PAIR JOIN ERROR] ❌ Code ${code.toUpperCase()} expired`);
        Alert.alert('Code Expired', 'This code has expired. Generate a new one on the other device.');
        return;
      }

      const upperCode = code.toUpperCase().trim();
      let pairingKey = data.pairingKey;
      let localUrl = data.localUrl;
      let globalUrl = data.globalUrl;
      let pin = data.pin;

      // ZERO-TRUST DECRYPTION: If encryptedData is present, decrypt using the 6-character code
      if (data.encryptedData) {
        try {
          const decrypted = await decrypt(data.encryptedData, upperCode);
          if (decrypted) {
            const parsed = JSON.parse(decrypted);
            pairingKey = parsed.pairingKey || pairingKey;
            localUrl = parsed.localUrl || localUrl;
            globalUrl = parsed.globalUrl || globalUrl;
            pin = parsed.pin || pin;
          }
        } catch (decErr) {
          syncLog('PAIR', `Decryption failed for code ${upperCode}: ${decErr}`);
        }
      }

      if (!pairingKey) {
        setIsPairing(false);
        Alert.alert('Pairing Failed', 'Could not read pairing credentials. Please verify the code and try again.');
        return;
      }

      syncLog('PAIR', `[STEP 4/6: PAIR JOIN 2/2] ✅ Found device '${data.deviceName}' (Key: ${pairingKey.substring(0, 8)}...) — executing pairing...`);
      await executePairing({
        key: pairingKey, local: localUrl, global: globalUrl,
        pin, name: data.deviceName, id: data.deviceId,
      });
      setIsConnectModalVisible(false);
      setPairingCodeInput('');
    } catch (err: any) {
      setIsPairing(false);
      const msg = err?.message || String(err);
      syncLog('PAIR', `[STEP 4/6: PAIR JOIN ERROR] ❌ Connection error: ${msg}`);
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
    abortControllerRef.current = new AbortController();
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
      // ZERO-TRUST: Encrypt pairingKey with the 6-character code so Firebase stores zero secrets
      const sensitivePayload = JSON.stringify({
        pairingKey: currentKey,
        localUrl: '',
        globalUrl: '',
        pin: '',
      });
      const encryptedData = await encrypt(sensitivePayload, code);

      const payload = {
        deviceId: myDeviceId,
        deviceName: deviceName || 'Phone',
        deviceType: 'Mobile',
        encryptedData,
        uid: auth.currentUser?.uid || '',
        timestamp: { '.sv': 'timestamp' },
      };
      const _pubToken = await getFirebaseIdToken();
      const writeUrl = `${firebaseDatabaseUrl}/pairing_codes/${code}.json${_pubToken ? `?auth=${_pubToken}` : ''}`;
      // C-1 FIX: Track signal for cleanup
      const writeSignal = createTimeoutSignal(10000);
      const writeRes = await fetch(writeUrl, {
        method: 'PUT',
        signal: writeSignal,
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });
      clearSignalTimeout(writeSignal);
      if (!writeRes.ok) {
        const errBody = await writeRes.text().catch(() => '');
        Alert.alert('Pairing Error', `Could not publish your code to the cloud (HTTP ${writeRes.status}).`);
        return;
      }
      // C-1 FIX: Track signal for cleanup
      const verifySignal = createTimeoutSignal(8000);
      const verifyRes = await fetch(`${firebaseDatabaseUrl}/pairing_codes/${code}.json${_pubToken ? `?auth=${_pubToken}` : ''}`, { signal: verifySignal });
      clearSignalTimeout(verifySignal);
      const verifyData = await verifyRes.json();
      if (!verifyData || (!verifyData.encryptedData && !verifyData.pairingKey)) {
        Alert.alert('Pairing Error', 'Code was written but could not be verified. Please try again.');
        return;
      }
      setMyPairingCode(code);
      toast.info(`Pairing Code: ${code}`, 'Active for 5 minutes — enter on your PC');

      if (connectionPollRef.current) clearInterval(connectionPollRef.current);
      if (connectionTimeoutRef.current) clearTimeout(connectionTimeoutRef.current);

      // Poll for incoming connections
      const pollForConnection = setInterval(async () => {
        try {
          const _pollToken = await getFirebaseIdToken();
          // C-1 FIX: Track signal for cleanup
          const pollSignal = createTimeoutSignal(10000);
          const codeRes = await fetch(
            `${firebaseDatabaseUrl}/pairing_codes/${code}.json${_pollToken ? `?auth=${_pollToken}` : ''}`,
            { signal: pollSignal }
          );
          clearSignalTimeout(pollSignal);
          const codeData = await codeRes.json();
          if (!codeData || !codeData.response) return;
          const resp = codeData.response;
          if (!resp.deviceId || !resp.deviceName) return;

          let respPairingKey = resp.pairingKey;
          let respLocalUrl = resp.localUrl;
          let respGlobalUrl = resp.globalUrl;

          // ZERO-TRUST: Decrypt response if encryptedData is present
          if (resp.encryptedData) {
            try {
              const decryptedResp = await decrypt(resp.encryptedData, code);
              if (decryptedResp) {
                const parsedResp = JSON.parse(decryptedResp);
                respPairingKey = parsedResp.pairingKey || respPairingKey;
                respLocalUrl = parsedResp.localUrl || respLocalUrl;
                respGlobalUrl = parsedResp.globalUrl || respGlobalUrl;
              }
            } catch (decRespErr) {
              syncLog('PAIR', `Response decryption error: ${decRespErr}`);
            }
          }
          if (!respPairingKey) return;

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

          if (respLocalUrl) {
            const normLocal = respLocalUrl.startsWith('http') ? respLocalUrl : `http://${respLocalUrl}`;
            await setSecureItem('pairedLocalUrl', normLocal);
            await AsyncStorage.setItem('@flyshelf_last_lan_url', normLocal).catch(() => {});
            cachedPcUrlRef.current = normLocal;
            cachedPcUrlTimestampRef.current = NetworkClock.now();
          }
          if (respGlobalUrl) await setSecureItem('pairedGlobalUrl', respGlobalUrl);
          const effectiveKey = respPairingKey || currentKey;
          if (effectiveKey) {
            clearKeyCache();
            pairingKeyRef.current = effectiveKey;
            await setSecureItem('pairingKey', effectiveKey);
            const uid = auth.currentUser?.uid;
            if (uid) {
              await set(ref(database, `members/${effectiveKey}/${uid}`), true).catch(() => {});
            }
          }

          setPairedPcName(resp.deviceName);
          if (!isGlobalSyncEnabledRef.current) setGlobalSyncEnabled(true);
          toast.success(`Paired with ${resp.deviceName}!`, 'Cross-device mesh sync is now active');

          clearInterval(pollForConnection);
          connectionPollRef.current = null;
          if (connectionTimeoutRef.current) { clearTimeout(connectionTimeoutRef.current); connectionTimeoutRef.current = null; }
          setMyPairingCode(null);
          try {
            const _delToken = await getFirebaseIdToken();
            // C-1 FIX: Track signal for cleanup
            const delSignal = createTimeoutSignal(10000);
            await fetch(`${firebaseDatabaseUrl}/pairing_codes/${code}.json${_delToken ? `?auth=${_delToken}` : ''}`, { method: 'DELETE', signal: delSignal });
            clearSignalTimeout(delSignal);
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
          // C-1 FIX: Track signal for cleanup
          const expSignal = createTimeoutSignal(10000);
          await fetch(`${firebaseDatabaseUrl}/pairing_codes/${code}.json${_expToken ? `?auth=${_expToken}` : ''}`, { method: 'DELETE', signal: expSignal });
          clearSignalTimeout(expSignal);
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
      toast.clipboard('QR Content Copied', data);
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
