/**
 * AES-256-GCM encryption for Firebase sync payloads.
 * Compatible with the PC-side SyncCrypto.cs implementation.
 * 
 * Key derivation: PBKDF2-SHA256 with 100,000 iterations from the pairing key.
 * Encrypted format: Base64(nonce(12B) + ciphertext + tag(16B))
 * 
 * Uses Web Crypto API directly for maximum compatibility with the PC's .NET AesGcm.
 * Both PC and Android use identical key derivation and encryption,
 * so encrypted items are interoperable across platforms.
 */

import { getSecureItem } from './secureStorage';
import { syncLog, setCryptoStrategy } from './debugLog';
import { Platform } from 'react-native';
import { base64ToUint8Array, uint8ArrayToBase64 } from './networkHelpers';

let _cryptoInstance: { subtle: SubtleCrypto; getRandomValues: (arr: Uint8Array) => Uint8Array } | null = null;
let _cryptoStrategy = 'none';

export const getCryptoStrategy = (): string => _cryptoStrategy;

const getCrypto = (): { subtle: SubtleCrypto; getRandomValues: (arr: Uint8Array) => Uint8Array } => {
  // Return cached instance if already resolved
  if (_cryptoInstance) return _cryptoInstance;

  if (Platform.OS === 'web') {
    const w = typeof crypto !== 'undefined' ? crypto : (globalThis as any).crypto;
    _cryptoInstance = w;
    _cryptoStrategy = 'web-crypto';
    return w;
  }

  try {
    const qc = require('react-native-quick-crypto');
    
    // Strategy 0: Use install() to polyfill global.crypto — most reliable
    if (typeof qc.install === 'function') {
      try {
        qc.install();
        if ((globalThis as any).crypto?.subtle && typeof (globalThis as any).crypto.subtle.importKey === 'function') {
          _cryptoInstance = { subtle: (globalThis as any).crypto.subtle, getRandomValues: (globalThis as any).crypto.getRandomValues?.bind((globalThis as any).crypto) || qc.getRandomValues };
          _cryptoStrategy = 'install-polyfill';
          syncLog('SYNC_CRYPTO', `✅ Crypto resolved via install() polyfill`);
          setCryptoStrategy(_cryptoStrategy);
          return _cryptoInstance;
        }
      } catch (installErr: any) {
        syncLog('SYNC_CRYPTO', `install() failed: ${installErr?.message}`);
      }
    }
    
    // Strategy 1: Named export (works if bundler resolves ESM re-exports)
    if (qc.subtle && typeof qc.subtle.importKey === 'function') {
      _cryptoInstance = { subtle: qc.subtle as SubtleCrypto, getRandomValues: qc.getRandomValues };
      _cryptoStrategy = 'named-export';
      syncLog('SYNC_CRYPTO', `✅ Crypto resolved via named export .subtle`);
      setCryptoStrategy(_cryptoStrategy);
      return _cryptoInstance;
    }
    
    // Strategy 2: Default export → .subtle
    if (qc.default?.subtle && typeof qc.default.subtle.importKey === 'function') {
      _cryptoInstance = { subtle: qc.default.subtle as SubtleCrypto, getRandomValues: qc.default.getRandomValues || qc.getRandomValues };
      _cryptoStrategy = 'default-export';
      syncLog('SYNC_CRYPTO', `✅ Crypto resolved via default.subtle`);
      setCryptoStrategy(_cryptoStrategy);
      return _cryptoInstance;
    }
    
    // Strategy 3: Subtle class is spread into module — instantiate it
    if (qc.Subtle && typeof qc.Subtle === 'function') {
      try {
        const subtleInstance = new qc.Subtle();
        if (typeof subtleInstance.importKey === 'function') {
          _cryptoInstance = { subtle: subtleInstance as SubtleCrypto, getRandomValues: qc.getRandomValues };
          _cryptoStrategy = 'subtle-instantiate';
          syncLog('SYNC_CRYPTO', `✅ Crypto resolved via new Subtle()`);
          setCryptoStrategy(_cryptoStrategy);
          return _cryptoInstance;
        }
      } catch {}
    }
    
    // Strategy 4: Individual functions spread at top level
    if (typeof qc.importKey === 'function' && typeof qc.deriveKey === 'function') {
      const manualSubtle = {
        importKey: qc.importKey.bind(qc),
        deriveKey: qc.deriveKey.bind(qc),
        encrypt: qc.encrypt?.bind(qc),
        decrypt: qc.decrypt?.bind(qc),
        deriveBits: qc.deriveBits?.bind(qc),
        sign: qc.sign?.bind(qc),
        verify: qc.verify?.bind(qc),
        digest: qc.digest?.bind(qc),
        generateKey: qc.generateKey?.bind(qc),
        exportKey: qc.exportKey?.bind(qc),
        wrapKey: qc.wrapKey?.bind(qc),
        unwrapKey: qc.unwrapKey?.bind(qc),
      } as unknown as SubtleCrypto;
      _cryptoInstance = { subtle: manualSubtle, getRandomValues: qc.getRandomValues };
      _cryptoStrategy = 'manual-top-level';
      syncLog('SYNC_CRYPTO', `✅ Crypto resolved via top-level functions`);
      setCryptoStrategy(_cryptoStrategy);
      return _cryptoInstance;
    }

    // Strategy 5: Directly require the subtle submodule
    try {
      const subtleMod = require('react-native-quick-crypto/lib/commonjs/subtle');
      if (subtleMod.subtle && typeof subtleMod.subtle.importKey === 'function') {
        _cryptoInstance = { subtle: subtleMod.subtle as SubtleCrypto, getRandomValues: qc.getRandomValues };
        _cryptoStrategy = 'direct-submodule';
        syncLog('SYNC_CRYPTO', `✅ Crypto resolved via direct submodule require`);
        setCryptoStrategy(_cryptoStrategy);
        return _cryptoInstance;
      }
      if (subtleMod.Subtle && typeof subtleMod.Subtle === 'function') {
        const inst = new subtleMod.Subtle();
        if (typeof inst.importKey === 'function') {
          _cryptoInstance = { subtle: inst as SubtleCrypto, getRandomValues: qc.getRandomValues };
          _cryptoStrategy = 'direct-submodule-instantiate';
          syncLog('SYNC_CRYPTO', `✅ Crypto resolved via direct submodule Subtle()`);
          setCryptoStrategy(_cryptoStrategy);
          return _cryptoInstance;
        }
      }
    } catch {}

    // Diagnostic: Log what keys are available so we can debug
    const relevantKeys = Object.keys(qc).filter(k => !k.startsWith('_'));
    syncLog('SYNC_CRYPTO', `❌ All ${relevantKeys.length} strategies failed. Module keys: ${relevantKeys.slice(0, 20).join(',')}`);
    if (qc.subtle) syncLog('SYNC_CRYPTO', `subtle type=${typeof qc.subtle}, keys=${Object.keys(qc.subtle || {}).slice(0, 10).join(',')}`);
  } catch (e: any) {
    syncLog('SYNC_CRYPTO', `❌ quick-crypto load failed: ${e?.message}`);
  }
  // Last resort: Web Crypto (works in Metro/Hermes dev)
  if (typeof crypto !== 'undefined' && (crypto as any).subtle) {
    _cryptoInstance = crypto as any;
    _cryptoStrategy = 'global-web-crypto';
    setCryptoStrategy(_cryptoStrategy);
    return _cryptoInstance!;
  }
  syncLog('SYNC_CRYPTO', `❌ No crypto implementation available`);
  throw new Error('No crypto implementation available');
};

// Must match PC-side constants in SyncCrypto.cs
const PBKDF2_ITERATIONS = 100_000;
const SALT_STRING = 'FlyShelf_v2.6.0_SyncSalt';
const KEY_SIZE_BITS = 256; // AES-256
const NONCE_SIZE = 12; // GCM standard
const TAG_SIZE = 16; // GCM auth tag

let _cachedKey: CryptoKey | null = null;
let _cachedPairingKey: string | null = null;

import AsyncStorage from '@react-native-async-storage/async-storage';

/**
 * Derives the AES-256 key from the pairing key using Web Crypto PBKDF2.
 * This is hardware-accelerated and produces identical output to .NET's Rfc2898DeriveBytes.
 * Cached for the lifetime of the pairing session.
 */
async function getKey(specificPairingKey?: string): Promise<CryptoKey> {
  let pairingKey = specificPairingKey;
  if (!pairingKey) {
    pairingKey = (await getSecureItem('pairingKey')) || (await AsyncStorage.getItem('pairingKey')) || (await AsyncStorage.getItem('@pairingKey')) || '';
  }
  if (!pairingKey) throw new Error('Cannot encrypt — no pairing key set');

  if (_cachedKey && _cachedPairingKey === pairingKey) return _cachedKey;

  const encoder = new TextEncoder();
  const passwordBytes = encoder.encode(pairingKey);
  const salt = encoder.encode(SALT_STRING);

  const cryptoInstance = getCrypto();

  // Import password as PBKDF2 key material
  const keyMaterial = await cryptoInstance.subtle.importKey(
    'raw', passwordBytes, 'PBKDF2', false, ['deriveKey']
  );

  // Derive AES-GCM key using PBKDF2-SHA256
  const derivedKey = await cryptoInstance.subtle.deriveKey(
    { name: 'PBKDF2', salt, iterations: PBKDF2_ITERATIONS, hash: 'SHA-256' },
    keyMaterial,
    { name: 'AES-GCM', length: KEY_SIZE_BITS },
    false,
    ['encrypt', 'decrypt']
  );
  if (!specificPairingKey) {
    _cachedKey = derivedKey;
    _cachedPairingKey = pairingKey;
  }
  return derivedKey;
}

/**
 * Encrypts plaintext using AES-256-GCM.
 * Returns Base64 string in format: nonce(12B) + ciphertext + tag(16B)
 * Compatible with PC-side SyncCrypto.Decrypt()
 * 
 * Note: Web Crypto AES-GCM produces ciphertext with tag appended,
 * so the output format is: nonce + (ciphertext || tag), which is identical
 * to the PC's format of nonce + ciphertext + tag.
 */
export async function encrypt(plaintext: string, specificPairingKey?: string): Promise<string> {
  try {
    if (!plaintext) return plaintext;
    
    // Do NOT catch errors here — let them propagate to the caller
    // so the caller can correctly set Encrypted: false when crypto fails.
    const key = await getKey(specificPairingKey);
    const encoder = new TextEncoder();
    const plaintextBytes = encoder.encode(plaintext);

    const cryptoInstance = getCrypto();

    // Generate random 12-byte nonce
    const nonce = new Uint8Array(NONCE_SIZE);
    cryptoInstance.getRandomValues(nonce);

    // Encrypt with AES-GCM (output = ciphertext + tag appended)
    const encryptedBuffer = await cryptoInstance.subtle.encrypt(
      { name: 'AES-GCM', iv: nonce, tagLength: TAG_SIZE * 8 },
      key,
      plaintextBytes
    );

    // Pack: nonce + ciphertextWithTag
    const encrypted = new Uint8Array(encryptedBuffer);
    const combined = new Uint8Array(NONCE_SIZE + encrypted.length);
    combined.set(nonce, 0);
    combined.set(encrypted, NONCE_SIZE);

    // Convert to base64
    return uint8ArrayToBase64(combined);
  } catch (err: any) {
    throw new Error(`Encryption failed: ${err?.message || 'unknown error'}`);
  }
}

/**
 * Decrypts AES-256-GCM ciphertext (Base64 encoded).
 * Returns null if decryption fails.
 * Compatible with PC-side SyncCrypto.Encrypt()
 * 
 * Expected input format: Base64(nonce(12B) + ciphertext + tag(16B))
 * Web Crypto expects: iv + ciphertextWithTag (tag appended to ciphertext)
 */
export async function decrypt(base64Ciphertext: string, specificPairingKey?: string): Promise<string | null> {
  if (!base64Ciphertext) return null;

  try {
    const key = await getKey(specificPairingKey);
    
    // Decode base64 to bytes
    const packed = base64ToUint8Array(base64Ciphertext);
    
    if (packed.length < NONCE_SIZE + TAG_SIZE) return null; // Too short

    // Extract nonce (first 12 bytes)
    const nonce = packed.slice(0, NONCE_SIZE);
    // Remaining bytes = ciphertext + tag (Web Crypto expects them combined)
    const ciphertextWithTag = packed.slice(NONCE_SIZE);

    const cryptoInstance = getCrypto();

    // Decrypt with AES-GCM
    const decryptedBuffer = await cryptoInstance.subtle.decrypt(
      { name: 'AES-GCM', iv: nonce, tagLength: TAG_SIZE * 8 },
      key,
      ciphertextWithTag
    );

    // Decode UTF-8
    const decoder = new TextDecoder();
    return decoder.decode(decryptedBuffer);
  } catch (e: any) {
    syncLog('SYNC_CRYPTO', `Decrypt failed: ${e?.message}`);
    return null; // Wrong key, tampered data, or unencrypted plaintext
  }
}

/**
 * Checks if a value appears to be AES-GCM encrypted.
 * Used for backward compatibility with unencrypted items.
 */
export function isEncrypted(value: string): boolean {
  if (!value || value.length < 40) return false;
  if (value.includes(' ') || value.includes('\n') || value.includes('\r')) return false;
  try {
    const decoded = base64ToUint8Array(value);
    return decoded.length >= NONCE_SIZE + TAG_SIZE; // nonce(12) + tag(16) minimum
  } catch {
    return false;
  }
}

/**
 * Clears the cached key (call when pairing key changes).
 */
export function clearKeyCache() {
  _cachedKey = null;
  _cachedPairingKey = null;
}

// Helpers removed — using optimized imports from networkHelpers.ts
