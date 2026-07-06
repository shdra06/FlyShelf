import { useState, useEffect } from 'react';
import { getSecureItem } from '../utils/secureStorage';

export interface UsePairingReturn {
  pairingCodeInput: string;
  setPairingCodeInput: (v: string) => void;
  myPairingCode: string | null;
  setMyPairingCode: (v: string | null) => void;
  isPairing: boolean;
  setIsPairing: (v: boolean) => void;
  pairedPcName: string | null;
  setPairedPcName: (v: string | null) => void;
}

/**
 * Manages pairing UI state: input code, generated code, pairing spinner, paired PC name.
 * Includes startup load of pairedPcName from secure storage and auto-clear on device removal.
 * Extracted from SyncScreen for decomposition.
 */
export function usePairing(pairedDevicesCount: number): UsePairingReturn {
  const [pairingCodeInput, setPairingCodeInput] = useState('');
  const [myPairingCode, setMyPairingCode] = useState<string | null>(null);
  const [isPairing, setIsPairing] = useState(false);
  const [pairedPcName, setPairedPcName] = useState<string | null>(null);

  // Load paired PC name on startup
  useEffect(() => {
    getSecureItem('pairedPcName').then(name => { if (name) setPairedPcName(name); }).catch(() => {});
  }, []);

  // Clear pairedPcName when all paired devices are removed in Settings
  useEffect(() => {
    if (pairedDevicesCount === 0) {
      setPairedPcName(null);
    }
  }, [pairedDevicesCount]);

  return {
    pairingCodeInput,
    setPairingCodeInput,
    myPairingCode,
    setMyPairingCode,
    isPairing,
    setIsPairing,
    pairedPcName,
    setPairedPcName,
  };
}
