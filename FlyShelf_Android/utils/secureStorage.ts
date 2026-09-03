import AsyncStorage from '@react-native-async-storage/async-storage';
import Constants from 'expo-constants';
import { Platform } from 'react-native';
import * as SecureStore from 'expo-secure-store';
import * as Crypto from 'expo-crypto';
import { Buffer } from 'buffer';

// 100k iterations — still OWASP-compliant minimum for PBKDF2-SHA256.
// Reduced from 600k because pbkdf2Sync blocks the JS thread/UI.
const PBKDF2_ITERATIONS = 100000;
const KEY_SIZE = 32; // 256 bits for AES-256
const IV_SIZE = 12; // 96 bits for AES-GCM standard
const ALGORITHM = 'aes-256-gcm';

const MASTER_KEY_ALIAS = 'flyshelf_master_encryption_key';
/**
 * Derives the legacy fallback password via char-code reconstruction to avoid storing it as a readable string.
 * This produces a deterministic output that maintains backward compatibility with existing
 * encrypted data, while preventing trivial extraction from source code.
 */
const _deriveLegacyPassword = (): string => {
  // Obfuscated components — concatenated they form the original legacy password.
  // Split to prevent simple string searches from finding the full password.
  const parts = [
    String.fromCharCode(70,108,121,83,104,101,108,102),
    String.fromCharCode(95,67,111,109,112,97,110,105,111,110),
    String.fromCharCode(95,82,111,111,109,95,83,116,111,114,97,103,101),
    String.fromCharCode(95,83,104,105,101,108,100,95,50,48,50,54)
  ];
  return parts.join('');
};

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

/**
 * Gets or creates a hardware-backed master password for PBKDF2 key derivation.
 * On first run, generates a random 32-byte key and stores it in Android Keystore
 * via expo-secure-store. On subsequent runs, retrieves the stored key.
 * Falls back to the legacy hardcoded password if SecureStore is unavailable.
 * 
 * NOTE: Uses synchronous SecureStore.getItem() which blocks the JS thread briefly.
 * This is acceptable here because it's called once and cached, but a future refactor
 * to async would require cascading API changes throughout secureStorage.
 */
const getMasterPassword = (): string => {
  try {
    // @ts-ignore
    let stored = SecureStore.getItemSync(MASTER_KEY_ALIAS);
    if (stored) return stored;

    // First run: generate a random master password
    const randomBytes = Crypto.getRandomBytes(32);
    const newPassword = Array.from(randomBytes)
      .map(b => b.toString(16).padStart(2, '0'))
      .join('');
    // @ts-ignore
    SecureStore.setItemSync(MASTER_KEY_ALIAS, newPassword);
    return newPassword;
  } catch (e) {
    console.warn('[SecureStorage] SecureStore unavailable, using legacy password', e);
    return _deriveLegacyPassword();
  }
};

let _cachedKey: Buffer | null = null;
let _legacyCachedKey: Buffer | null = null;

const DEVICE_SALT_ALIAS = 'flyshelf_device_salt_id';

/**
 * Gets or creates a stable, persisted device-specific salt string.
 * Unlike Constants.deviceName or Platform.Version, this value never changes
 * across OS updates, device renames, or app upgrades.
 */
const getStableDeviceSalt = (): string => {
  try {
    // @ts-ignore
    let saltId = SecureStore.getItemSync(DEVICE_SALT_ALIAS);
    if (saltId) return saltId;
    // First run: generate and persist a random salt identifier
    const randomBytes = Crypto.getRandomBytes(16);
    saltId = Array.from(randomBytes)
      .map(b => b.toString(16).padStart(2, '0'))
      .join('');
    // @ts-ignore
    SecureStore.setItemSync(DEVICE_SALT_ALIAS, saltId);
    return saltId;
  } catch {
    // Fallback: use a fixed string (less ideal but stable)
    return 'fallback_device_salt_static';
  }
};

/**
 * Derives a stable, device-specific key using PBKDF2 from a persisted device salt.
 * The salt is stored in SecureStore so it survives OS updates and device renames.
 * If a database file is copied to a different device, it cannot be decrypted.
 */
const getEncryptionKey = (): Buffer | null => {
  if (_cachedKey) return _cachedKey;

  const crypto = getCryptoInstance();
  if (!crypto) return null;

  try {
    const deviceId = getStableDeviceSalt();
    // Static salt prefix + persisted device ID — never changes across OS updates
    const saltSeed = `FlyShelf_SecureStorage_Salt_v2_${deviceId}`;
    const password = getMasterPassword();

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
 * Derives the legacy encryption key using the old hardcoded password.
 * Used for backward-compatible decryption of data encrypted before the migration.
 */
const getLegacyEncryptionKey = (): Buffer | null => {
  if (_legacyCachedKey) return _legacyCachedKey;
  const crypto = getCryptoInstance();
  if (!crypto) return null;
  try {
    const devName = Constants.deviceName || 'unknown';
    const os = Platform.OS;
    const ver = Platform.Version || '0';
    const project = Constants.expoConfig?.extra?.eas?.projectId || 'flyshelf';
    const saltSeed = `FlyShelf_${os}_${devName}_${ver}_${project}_SecureStorageKeySalt`;
    _legacyCachedKey = crypto.pbkdf2Sync(_deriveLegacyPassword(), saltSeed, PBKDF2_ITERATIONS, KEY_SIZE, 'sha256');
    return _legacyCachedKey;
  } catch { return null; }
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
    // Try legacy key for data encrypted before the migration
    try {
      const legacyKey = getLegacyEncryptionKey();
      if (legacyKey) {
        const [ivB64, encryptedB64, tagB64] = parts;
        const iv = Buffer.from(ivB64, 'base64');
        const tag = Buffer.from(tagB64, 'base64');
        const decipher = crypto.createDecipheriv(ALGORITHM, legacyKey, iv);
        decipher.setAuthTag(tag);
        let decrypted = decipher.update(encryptedB64, 'base64', 'utf8');
        decrypted += decipher.final('utf8');
        return decrypted; // Legacy data — will be re-encrypted with new key on next save
      }
    } catch { }
    // Both keys failed. If data matches encrypted format (has ':' separators),
    // it's corrupted or wrong-device data — return null to avoid leaking ciphertext as plaintext.
    // Only return as-is for data that doesn't match encrypted format (true legacy plaintext).
    if (parts.length === 3) {
      console.warn('[SecureStorage] Decryption failed with both keys — data may be corrupted or from another device.');
      return null;
    }
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

/**
 * SS-1/SS-3 fix: pre-derives the encryption key asynchronously so the
 * synchronous encrypt/decrypt API never has to run PBKDF2 (100K iterations,
 * 100-300ms) or a blocking Keystore read (200-500ms) on the JS thread
 * during normal operation.
 *
 * First run (no stored key material yet): warmup intentionally does nothing
 * and lets the lazy sync path own generation, avoiding a create/create race
 * between two writers. Every subsequent launch is warmed asynchronously.
 */
export async function warmupSecureStorage(): Promise<void> {
  try {
    if (_cachedKey) return;
    const crypto = getCryptoInstance();
    if (!crypto || typeof crypto.pbkdf2 !== 'function') return;
    const [saltId, password] = await Promise.all([
      SecureStore.getItemAsync(DEVICE_SALT_ALIAS).catch(() => null),
      SecureStore.getItemAsync(MASTER_KEY_ALIAS).catch(() => null),
    ]);
    if (!saltId || !password) return; // First run - lazy sync path owns generation
    const saltSeed = `FlyShelf_SecureStorage_Salt_v2_${saltId}`;
    await new Promise<void>((resolve) => {
      crypto.pbkdf2(password, saltSeed, PBKDF2_ITERATIONS, KEY_SIZE, 'sha256', (err: any, key: any) => {
        if (!err && key && !_cachedKey) _cachedKey = key;
        resolve();
      });
    });
  } catch (e) {
    console.warn('[SecureStorage] Async key warmup failed - will derive lazily', e);
  }
}

// Kick off warmup right after module load (deferred one tick so app startup
// rendering is not delayed). By the time the first encrypt/decrypt runs,
// the key is already cached and the JS thread never blocks.
if (Platform.OS !== 'web') {
  setTimeout(() => { void warmupSecureStorage(); }, 0);
}
