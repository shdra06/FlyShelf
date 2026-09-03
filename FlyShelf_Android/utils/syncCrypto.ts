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
import { syncLog } from './debugLog';
import { Platform } from 'react-native';
import { base64ToUint8Array, uint8ArrayToBase64 } from './networkHelpers';

const getCrypto = (): { subtle: SubtleCrypto; getRandomValues: (arr: Uint8Array) => Uint8Array } => {
  if (Platform.OS === 'web') {
    const w = typeof crypto !== 'undefined' ? crypto : (globalThis as any).crypto;
    return w;
  }
  try {
    // react-native-quick-crypto: named exports at module root
    const { subtle, getRandomValues } = require('react-native-quick-crypto');
    if (subtle && typeof subtle.importKey === 'function') {
      return { subtle: subtle as SubtleCrypto, getRandomValues };
    }
    // Fallback: try .default
    const qc = require('react-native-quick-crypto');
    const subtleObj = qc?.default?.subtle || qc?.subtle;
    const grv = qc?.default?.getRandomValues || qc?.getRandomValues;
    if (subtleObj) return { subtle: subtleObj as SubtleCrypto, getRandomValues: grv };
  } catch {}
  // Last resort: Web Crypto (works in Metro/Hermes dev)
  if (typeof crypto !== 'undefined' && crypto.subtle) return crypto as any;
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
