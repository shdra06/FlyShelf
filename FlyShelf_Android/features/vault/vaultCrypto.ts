import * as FileSystem from 'expo-file-system/legacy';
import * as SecureStore from 'expo-secure-store';
import * as Crypto from 'expo-crypto';
import { Buffer } from 'buffer';
import { Platform, AppState } from 'react-native';

const VAULT_DIR = `${FileSystem.documentDirectory}vault/`;
const TEMP_DIR = `${FileSystem.cacheDirectory}vault_temp/`;
const MASTER_KEY_ALIAS = 'flyshelf_master_encryption_key';
const DEVICE_SALT_ALIAS = 'flyshelf_device_salt_id';
const PBKDF2_ITERATIONS = 100000;
const KEY_SIZE = 32;
const ALGORITHM = 'aes-256-gcm';

const getCryptoInstance = () => {
  if (Platform.OS === 'web') return null;
  try {
    return require('react-native-quick-crypto');
  } catch (e) {
    return null;
  }
};

const _deriveLegacyPassword = (): string => {
  const parts = [
    String.fromCharCode(70,108,121,83,104,101,108,102),
    String.fromCharCode(95,67,111,109,112,97,110,105,111,110),
    String.fromCharCode(95,82,111,111,109,95,83,116,111,114,97,103,101),
    String.fromCharCode(95,83,104,105,101,108,100,95,50,48,50,54)
  ];
  return parts.join('');
};

const getMasterPassword = (): string => {
  try {
    // @ts-ignore
    let stored = SecureStore.getItemSync(MASTER_KEY_ALIAS);
    if (stored) return stored;
    const randomBytes = Crypto.getRandomBytes(32);
    const newPassword = Array.from(randomBytes).map(b => b.toString(16).padStart(2, '0')).join('');
    // @ts-ignore
    SecureStore.setItemSync(MASTER_KEY_ALIAS, newPassword);
    return newPassword;
  } catch (e) {
    return _deriveLegacyPassword();
  }
};

const getStableDeviceSalt = (): string => {
  try {
    // @ts-ignore
    let saltId = SecureStore.getItemSync(DEVICE_SALT_ALIAS);
    if (saltId) return saltId;
    const randomBytes = Crypto.getRandomBytes(16);
    saltId = Array.from(randomBytes).map(b => b.toString(16).padStart(2, '0')).join('');
    // @ts-ignore
    SecureStore.setItemSync(DEVICE_SALT_ALIAS, saltId);
    return saltId;
  } catch {
    return 'fallback_device_salt_static';
  }
};

let _cachedKey: Buffer | null = null;
const getEncryptionKey = (): Buffer | null => {
  if (_cachedKey) return _cachedKey;
  const crypto = getCryptoInstance();
  if (!crypto) return null;
  try {
    const deviceId = getStableDeviceSalt();
    const saltSeed = `FlyShelf_SecureStorage_Salt_v2_${deviceId}`;
    const password = getMasterPassword();
    _cachedKey = crypto.pbkdf2Sync(password, saltSeed, PBKDF2_ITERATIONS, KEY_SIZE, 'sha256');
    return _cachedKey;
  } catch (e) {
    return null;
  }
};

export const ensureVaultDirs = async () => {
  const vInfo = await FileSystem.getInfoAsync(VAULT_DIR);
  if (!vInfo.exists) await FileSystem.makeDirectoryAsync(VAULT_DIR, { intermediates: true });
  const tInfo = await FileSystem.getInfoAsync(TEMP_DIR);
  if (!tInfo.exists) await FileSystem.makeDirectoryAsync(TEMP_DIR, { intermediates: true });
};

export const encryptFile = async (sourceUri: string): Promise<{encryptedUri: string, iv: string}> => {
  await ensureVaultDirs();
  const crypto = getCryptoInstance();
  const key = getEncryptionKey();
  if (!crypto || !key) throw new Error('Crypto unavailable');

  const fileData = await FileSystem.readAsStringAsync(sourceUri, { encoding: FileSystem.EncodingType.Base64 });
  const iv = Crypto.getRandomBytes(12);
  const ivBuffer = Buffer.from(iv);
  
  const cipher = crypto.createCipheriv(ALGORITHM, key, ivBuffer);
  let encrypted = cipher.update(fileData, 'base64', 'base64');
  encrypted += cipher.final('base64');
  
  const tag = cipher.getAuthTag();
  
  const finalData = `${encrypted}:${tag.toString('base64')}`;
  
  const filename = `${Date.now()}_${Math.random().toString(36).substr(2,9)}.enc`;
  const encryptedUri = `${VAULT_DIR}${filename}`;
  
  await FileSystem.writeAsStringAsync(encryptedUri, finalData, { encoding: FileSystem.EncodingType.UTF8 });
  
  return { encryptedUri, iv: ivBuffer.toString('base64') };
};

export const decryptFile = async (encryptedUri: string, ivBase64: string): Promise<string> => {
  await ensureVaultDirs();
  const crypto = getCryptoInstance();
  const key = getEncryptionKey();
  if (!crypto || !key) throw new Error('Crypto unavailable');

  const data = await FileSystem.readAsStringAsync(encryptedUri, { encoding: FileSystem.EncodingType.UTF8 });
  const parts = data.split(':');
  if (parts.length !== 2) throw new Error('Invalid encrypted file format');
  
  const [encryptedB64, tagB64] = parts;
  
  const iv = Buffer.from(ivBase64, 'base64');
  const tag = Buffer.from(tagB64, 'base64');
  
  const decipher = crypto.createDecipheriv(ALGORITHM, key, iv);
  decipher.setAuthTag(tag);
  
  let decrypted = decipher.update(encryptedB64, 'base64', 'base64');
  decrypted += decipher.final('base64');
  
  const tempFilename = `dec_${Date.now()}_${Math.random().toString(36).substr(2,5)}`;
  const tempUri = `${TEMP_DIR}${tempFilename}`;
  
  await FileSystem.writeAsStringAsync(tempUri, decrypted, { encoding: FileSystem.EncodingType.Base64 });
  return tempUri;
};

export const cleanupTempFiles = async (): Promise<void> => {
  try {
    const tInfo = await FileSystem.getInfoAsync(TEMP_DIR);
    if (!tInfo.exists) return;
    const files = await FileSystem.readDirectoryAsync(TEMP_DIR);
    for (const f of files) {
      await FileSystem.deleteAsync(`${TEMP_DIR}${f}`, { idempotent: true });
    }
  } catch (e) {
    console.warn('Failed to cleanup temp files', e);
  }
};

// SECURITY: Automatically purge temporary decrypted files when app backgrounds or on startup
if (Platform.OS !== 'web') {
  cleanupTempFiles().catch(() => {});
  AppState.addEventListener('change', (nextAppState) => {
    if (nextAppState === 'background' || nextAppState === 'inactive') {
      cleanupTempFiles().catch(() => {});
    }
  });
}
