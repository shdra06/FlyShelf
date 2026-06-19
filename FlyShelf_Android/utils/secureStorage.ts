import AsyncStorage from '@react-native-async-storage/async-storage';
import Constants from 'expo-constants';
import { Platform } from 'react-native';

const PBKDF2_ITERATIONS = 600000;
const KEY_SIZE = 32; // 256 bits for AES-256
const IV_SIZE = 12; // 96 bits for AES-GCM standard
const ALGORITHM = 'aes-256-gcm';

// Dynamically import react-native-quick-crypto on Native platforms
const getCryptoInstance = () => {
  if (Platform.OS === 'web') {
    return null;
  }
  try {
    return require('react-native-quick-crypto');
  } catch (e) {
    console.warn('[SecureStorage] Failed to load react-native-quick-crypto, using plaintext fallback.', e);
    return null;
  }
};

let _cachedKey: Buffer | null = null;

/**
 * Derives a stable, device-specific key using PBKDF2 from unique device signatures.
 * If a database file is copied to a different device, it cannot be decrypted.
 */
const getEncryptionKey = (): Buffer | null => {
  if (_cachedKey) return _cachedKey;

  const crypto = getCryptoInstance();
  if (!crypto) return null;

  try {
    const devName = Constants.deviceName || 'unknown';
    const os = Platform.OS;
    const ver = Platform.Version || '0';
    const project = Constants.expoConfig?.extra?.eas?.projectId || 'flyshelf';
    
    // Stable device-specific signature acting as our salt
    const saltSeed = `FlyShelf_${os}_${devName}_${ver}_${project}_SecureStorageKeySalt`;
    const password = 'FlyShelf_Companion_Room_Storage_Shield_2026';

    _cachedKey = crypto.pbkdf2Sync(
      password,
      saltSeed,
      PBKDF2_ITERATIONS,
      KEY_SIZE,
      'sha256'
    );
    return _cachedKey;
  } catch (e) {
    console.error('[SecureStorage] Failed to derive stable encryption key', e);
    return null;
  }
};

/**
 * Encrypts plaintext using AES-256-GCM.
 * Output format: base64(iv) + ":" + ciphertext + ":" + base64(tag)
 */
export function encrypt(plaintext: string): string {
  if (!plaintext) return plaintext;

  const crypto = getCryptoInstance();
  const key = getEncryptionKey();
  if (!crypto || !key) {
    console.error('[SecureStorage] Crypto unavailable — cannot encrypt. Data will not be stored.');
    throw new Error('Encryption unavailable on this platform.');
  }

  try {
    const iv = crypto.randomBytes(IV_SIZE);
    const cipher = crypto.createCipheriv(ALGORITHM, key, iv);
    
    let encrypted = cipher.update(plaintext, 'utf8', 'base64');
    encrypted += cipher.final('base64');
    
    const tag = cipher.getAuthTag();
    
    return `${iv.toString('base64')}:${encrypted}:${tag.toString('base64')}`;
  } catch (e) {
    console.error('[SecureStorage] Encryption failed', e);
    throw new Error('Encryption failed — data will not be stored insecurely.');
  }
}

/**
 * Decrypts AES-256-GCM ciphertext.
 * Robustly falls back to returning the input as plaintext if decryption fails or
 * if it doesn't match the encrypted structure (providing perfect backward compatibility).
 */
export function decrypt(ciphertext: string): string | null {
  if (!ciphertext) return null;
  
  const crypto = getCryptoInstance();
  const key = getEncryptionKey();
  if (!crypto || !key) {
    return ciphertext;
  }

  const parts = ciphertext.split(':');
  if (parts.length !== 3) {
    // Appears to be unencrypted legacy plaintext
    return ciphertext;
  }

  try {
    const [ivB64, encryptedB64, tagB64] = parts;
    const iv = Buffer.from(ivB64, 'base64');
    const tag = Buffer.from(tagB64, 'base64');
    
    const decipher = crypto.createDecipheriv(ALGORITHM, key, iv);
    decipher.setAuthTag(tag);
    
    let decrypted = decipher.update(encryptedB64, 'base64', 'utf8');
    decrypted += decipher.final('utf8');
    
    return decrypted;
  } catch (e) {
    // If decryption fails (e.g. due to key mismatch or corrupted cipher),
    // return as-is so legacy plaintext works.
    return ciphertext;
  }
}

/**
 * Encrypted set wrapper for AsyncStorage.
 */
export async function setSecureItem(key: string, value: string): Promise<void> {
  if (value === null || value === undefined) {
    await AsyncStorage.removeItem(key);
    return;
  }
  const encryptedValue = encrypt(value);
  await AsyncStorage.setItem(key, encryptedValue);
}

/**
 * Decrypted get wrapper for AsyncStorage.
 */
export async function getSecureItem(key: string): Promise<string | null> {
  const value = await AsyncStorage.getItem(key);
  if (!value) return null;
  return decrypt(value);
}

/**
 * Remove wrapper for AsyncStorage.
 */
export async function removeSecureItem(key: string): Promise<void> {
  await AsyncStorage.removeItem(key);
}
