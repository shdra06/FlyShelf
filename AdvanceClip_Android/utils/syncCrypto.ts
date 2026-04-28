/**
 * AES-256-GCM encryption for Firebase sync payloads.
 * Compatible with the PC-side SyncCrypto.cs implementation.
 * 
 * Key derivation: PBKDF2-SHA256 with 100,000 iterations from the pairing key.
 * Encrypted format: Base64(nonce(12B) + ciphertext + tag(16B))
 * 
 * Both PC and Android use identical key derivation and encryption,
 * so encrypted items are interoperable across platforms.
 */

import { AESEncryptionKey, AESSealedData, aesEncryptAsync, aesDecryptAsync, digestStringAsync, CryptoDigestAlgorithm } from 'expo-crypto';
import AsyncStorage from '@react-native-async-storage/async-storage';

// Must match PC-side constants in SyncCrypto.cs
const PBKDF2_ITERATIONS = 100_000;
const SALT_STRING = 'AdvanceClip_v2.6.0_SyncSalt';
const KEY_SIZE_BYTES = 32; // AES-256

let _cachedKey: AESEncryptionKey | null = null;
let _cachedPairingKey: string | null = null;

/**
 * Converts a hex string to Uint8Array.
 */
function hexToBytes(hex: string): Uint8Array {
  const bytes = new Uint8Array(hex.length / 2);
  for (let i = 0; i < hex.length; i += 2) {
    bytes[i / 2] = parseInt(hex.substr(i, 2), 16);
  }
  return bytes;
}

/**
 * Converts Uint8Array to hex string.
 */
function bytesToHex(bytes: Uint8Array): string {
  return Array.from(bytes).map(b => b.toString(16).padStart(2, '0')).join('');
}

/**
 * PBKDF2-SHA256 key derivation (pure JS implementation matching .NET's Rfc2898DeriveBytes).
 * This produces the exact same 32-byte key as the PC side for the same pairing key.
 */
async function pbkdf2Sha256(password: string, salt: Uint8Array, iterations: number, keyLength: number): Promise<Uint8Array> {
  const encoder = new TextEncoder();
  const passwordBytes = encoder.encode(password);

  // PBKDF2 with HMAC-SHA256
  const numBlocks = Math.ceil(keyLength / 32); // SHA-256 = 32 bytes
  const result = new Uint8Array(keyLength);

  for (let blockIdx = 1; blockIdx <= numBlocks; blockIdx++) {
    // U1 = HMAC-SHA256(password, salt || INT32_BE(blockIdx))
    const blockInput = new Uint8Array(salt.length + 4);
    blockInput.set(salt, 0);
    blockInput[salt.length] = (blockIdx >>> 24) & 0xff;
    blockInput[salt.length + 1] = (blockIdx >>> 16) & 0xff;
    blockInput[salt.length + 2] = (blockIdx >>> 8) & 0xff;
    blockInput[salt.length + 3] = blockIdx & 0xff;

    let u = await hmacSha256(passwordBytes, blockInput);
    const block = new Uint8Array(u);

    for (let i = 1; i < iterations; i++) {
      u = await hmacSha256(passwordBytes, u);
      for (let j = 0; j < block.length; j++) {
        block[j] ^= u[j];
      }
    }

    const offset = (blockIdx - 1) * 32;
    const copyLen = Math.min(32, keyLength - offset);
    result.set(block.subarray(0, copyLen), offset);
  }

  return result;
}

/**
 * HMAC-SHA256 using Web Crypto API (available in React Native via Hermes).
 */
async function hmacSha256(key: Uint8Array, message: Uint8Array): Promise<Uint8Array> {
  const cryptoKey = await crypto.subtle.importKey(
    'raw',
    key,
    { name: 'HMAC', hash: 'SHA-256' },
    false,
    ['sign']
  );
  const signature = await crypto.subtle.sign('HMAC', cryptoKey, message);
  return new Uint8Array(signature);
}

/**
 * Derives the AES-256 key from the pairing key.
 * Cached for the lifetime of the pairing session.
 */
async function getKey(): Promise<AESEncryptionKey> {
  const pairingKey = await AsyncStorage.getItem('pairingKey');
  if (!pairingKey) throw new Error('Cannot encrypt — no pairing key set');

  if (_cachedKey && _cachedPairingKey === pairingKey) return _cachedKey;

  const encoder = new TextEncoder();
  const salt = encoder.encode(SALT_STRING);
  const keyBytes = await pbkdf2Sha256(pairingKey, salt, PBKDF2_ITERATIONS, KEY_SIZE_BYTES);

  _cachedKey = await AESEncryptionKey.import(keyBytes);
  _cachedPairingKey = pairingKey;
  return _cachedKey;
}

/**
 * Encrypts plaintext using AES-256-GCM.
 * Returns Base64 string in format: nonce(12B) + ciphertext + tag(16B)
 * Compatible with PC-side SyncCrypto.Decrypt()
 */
export async function encrypt(plaintext: string): Promise<string> {
  if (!plaintext) return plaintext;
  const key = await getKey();

  // Encode plaintext to base64 (expo-crypto expects base64 input)
  const plaintextBase64 = btoa(unescape(encodeURIComponent(plaintext)));

  const sealedData = await aesEncryptAsync(plaintextBase64, key);

  // Get combined format: IV + ciphertext + tag (as base64)
  const combined = await sealedData.combined('base64');
  return combined;
}

/**
 * Decrypts AES-256-GCM ciphertext (Base64 encoded).
 * Returns null if decryption fails.
 * Compatible with PC-side SyncCrypto.Encrypt()
 */
export async function decrypt(base64Ciphertext: string): Promise<string | null> {
  if (!base64Ciphertext) return base64Ciphertext;

  try {
    const key = await getKey();

    // Parse combined format: IV(12B) + ciphertext + tag(16B)
    const sealedData = AESSealedData.fromCombined(base64Ciphertext);

    const decryptedBase64 = await aesDecryptAsync(sealedData, key, { output: 'base64' });

    // Decode from base64 back to UTF-8 string
    return decodeURIComponent(escape(atob(decryptedBase64 as string)));
  } catch {
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
    const decoded = atob(value);
    return decoded.length >= 28; // nonce(12) + tag(16) minimum
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
