/**
 * EncryptedStorage — Drop-in encrypted replacement for AsyncStorage.
 * 
 * All values are AES-256-GCM encrypted via secureStorage's encrypt/decrypt
 * before being stored in AsyncStorage. Keys are stored as-is (not encrypted)
 * since they are application-defined constants, not user data.
 * 
 * Migration: When reading data, if decryption fails (returns null) but the
 * raw value exists and doesn't match encrypted format, it's treated as 
 * legacy plaintext data. It will be re-encrypted on the next write.
 * 
 * Thread safety: Uses the same cached key as secureStorage — warm it up
 * at app start with warmupSecureStorage().
 */

import AsyncStorage from '@react-native-async-storage/async-storage';
import { encrypt, decrypt } from './secureStorage';

// Keys that should NOT be encrypted (boolean flags, timestamps, etc.)
const PLAINTEXT_KEYS = new Set([
  '@flyshelf_onboarding_done',
  'localWipeTimestamp',
  'lastNotifiedTimestamp',
  '@isGlobalSyncEnabled',
  '@isFloatingBallEnabled',
  '@floatingBallSize',
  '@floatingBallAutoHide',
  '@deviceId',
  '@deviceName',
  'last_crash_error',
]);

// Keys that MUST NEVER be stored in plaintext under any circumstances (fail-closed)
const SENSITIVE_KEYS = new Set([
  'pairingKey',
  '@flyshelf_vault_manifest',
  'flyshelf_master_encryption_key',
  'firebase_auth_token',
  'pairedGlobalUrl',
  'pairedLocalUrl',
  'webClientPinToken',
]);

const EncryptedStorage = {
  async getItem(key: string): Promise<string | null> {
    const raw = await AsyncStorage.getItem(key);
    if (raw === null) return null;
    if (PLAINTEXT_KEYS.has(key)) {
      if (raw && raw.includes(':') && raw.split(':').length === 3) {
        try {
          const decrypted = decrypt(raw);
          if (decrypted) return decrypted;
        } catch {}
      }
      return raw;
    }
    try {
      const decrypted = decrypt(raw);
      // decrypt returns null if both keys fail but format matches encrypted
      // decrypt returns the raw string if it doesn't match encrypted format (legacy plaintext)
      return decrypted;
    } catch {
      // Crypto not available — return raw value (graceful degradation)
      return raw;
    }
  },

  async setItem(key: string, value: string): Promise<void> {
    if (PLAINTEXT_KEYS.has(key)) {
      await AsyncStorage.setItem(key, value);
      return;
    }
    try {
      const encrypted = encrypt(value);
      await AsyncStorage.setItem(key, encrypted);
    } catch {
      // SECURITY: If this is a sensitive key, do NOT write plaintext! Fail closed!
      if (SENSITIVE_KEYS.has(key) || key.toLowerCase().includes('key') || key.toLowerCase().includes('token') || key.toLowerCase().includes('vault')) {
        console.error(`[EncryptedStorage] Encryption failed for sensitive key '${key}' — failing closed`);
        throw new Error(`Encryption failed for sensitive key '${key}'`);
      }
      // Non-sensitive: fallback to plaintext
      console.warn(`[EncryptedStorage] Encryption failed for key '${key}' — storing as plaintext`);
      await AsyncStorage.setItem(key, value);
    }
  },

  async removeItem(key: string): Promise<void> {
    await AsyncStorage.removeItem(key);
  },

  async multiGet(keys: readonly string[]): Promise<readonly [string, string | null][]> {
    const results = await AsyncStorage.multiGet(keys);
    return Promise.all(
      results.map(async ([key, raw]) => {
        if (raw === null) return [key, null] as [string, string | null];
        if (PLAINTEXT_KEYS.has(key)) {
          if (raw && raw.includes(':') && raw.split(':').length === 3) {
            try {
              const decrypted = decrypt(raw);
              if (decrypted) return [key, decrypted] as [string, string | null];
            } catch {}
          }
          return [key, raw] as [string, string | null];
        }
        try {
          return [key, decrypt(raw)] as [string, string | null];
        } catch {
          return [key, raw] as [string, string | null];
        }
      })
    );
  },

  async getAllKeys(): Promise<readonly string[]> {
    return AsyncStorage.getAllKeys();
  },

  async clear(): Promise<void> {
    await AsyncStorage.clear();
  },
};

export default EncryptedStorage;
