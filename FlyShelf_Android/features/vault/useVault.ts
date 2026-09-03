import { useState, useEffect } from 'react';
import { VaultManifest, VaultEntry, VaultCategory } from './vaultTypes';
import { encryptFile, decryptFile, cleanupTempFiles, ensureVaultDirs } from './vaultCrypto';
import EncryptedStorage from '../../utils/EncryptedStorage';
import * as FileSystem from 'expo-file-system/legacy';
import * as IntentLauncher from 'expo-intent-launcher';
import * as Sharing from 'expo-sharing';
import { Platform } from 'react-native';
import { toast } from '../../context/ToastContext';
import { fuzzyIsMatch } from '../../utils/textNormalize';

const MANIFEST_KEY = '@flyshelf_vault_manifest';

const DEFAULT_CATEGORIES: VaultCategory[] = [
  { id: 'cat_docs', name: 'Documents', icon: '📄', color: '#60A5FA', fileCount: 0 },
  { id: 'cat_ids', name: 'IDs & Cards', icon: '🆔', color: '#FBBF24', fileCount: 0 },
  { id: 'cat_finance', name: 'Finance', icon: '💰', color: '#34D399', fileCount: 0 },
  { id: 'cat_health', name: 'Health', icon: '🏥', color: '#F87171', fileCount: 0 },
  { id: 'cat_education', name: 'Education', icon: '📚', color: '#A78BFA', fileCount: 0 },
  { id: 'cat_personal', name: 'Personal', icon: '🔒', color: '#8B92A0', fileCount: 0 }
];

export const useVault = () => {
  const [manifest, setManifest] = useState<VaultManifest | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  const loadManifest = async () => {
    try {
      await ensureVaultDirs();
      const data = await EncryptedStorage.getItem(MANIFEST_KEY);
      if (data) {
        const parsed = JSON.parse(data);
        const counts = parsed.entries.reduce((acc: any, e: any) => {
          acc[e.categoryId] = (acc[e.categoryId] || 0) + 1;
          return acc;
        }, {});
        parsed.categories.forEach((c: any) => c.fileCount = counts[c.id] || 0);
        setManifest(parsed);
      } else {
        setManifest({
          version: 1,
          categories: DEFAULT_CATEGORIES,
          entries: [],
          lastModified: Date.now()
        });
      }
    } catch (e) {
      console.error('Failed to load vault manifest', e);
      toast.error('Vault Error', 'Could not load vault data');
    } finally {
      setIsLoading(false);
    }
  };

  useEffect(() => {
    loadManifest();
    return () => { cleanupTempFiles(); };
  }, []);

  const saveManifest = async (newManifest: VaultManifest) => {
    try {
      newManifest.lastModified = Date.now();
      const counts = newManifest.entries.reduce((acc: any, e: any) => {
        acc[e.categoryId] = (acc[e.categoryId] || 0) + 1;
        return acc;
      }, {});
      newManifest.categories.forEach((c: any) => c.fileCount = counts[c.id] || 0);
      
      await EncryptedStorage.setItem(MANIFEST_KEY, JSON.stringify(newManifest));
      setManifest(newManifest);
    } catch (e) {
      toast.error('Save Error', 'Could not save vault changes');
    }
  };

  const addFile = async (sourceUri: string, fileName: string, mimeType: string, categoryId: string, fileSize: number = 0) => {
    if (!manifest) return;
    try {
      const { encryptedUri, iv } = await encryptFile(sourceUri);
      const newEntry: VaultEntry = {
        id: `ve_${Date.now()}_${Math.random().toString(36).substr(2,6)}`,
        originalName: fileName,
        encryptedFilename: encryptedUri.split('/').pop() || '',
        mimeType,
        fileSize,
        categoryId,
        dateAdded: Date.now(),
        iv
      };
      await saveManifest({
        ...manifest,
        entries: [...manifest.entries, newEntry]
      });
      toast.success('File Secured', 'File encrypted and added to Vault');
    } catch (e) {
      console.error(e);
      toast.error('Encryption Failed', 'Could not encrypt file');
    }
  };

  const removeFile = async (entryId: string) => {
    if (!manifest) return;
    const entry = manifest.entries.find(e => e.id === entryId);
    if (!entry) return;
    try {
      const VAULT_DIR = `${FileSystem.documentDirectory}vault/`;
      await FileSystem.deleteAsync(`${VAULT_DIR}${entry.encryptedFilename}`, { idempotent: true });
      await saveManifest({
        ...manifest,
        entries: manifest.entries.filter(e => e.id !== entryId)
      });
      toast.success('File Removed', 'File permanently deleted from Vault');
    } catch (e) {
      toast.error('Error', 'Could not delete file');
    }
  };

  const openFile = async (entry: VaultEntry) => {
    try {
      const VAULT_DIR = `${FileSystem.documentDirectory}vault/`;
      const tempUri = await decryptFile(`${VAULT_DIR}${entry.encryptedFilename}`, entry.iv);
      const ext = entry.originalName.split('.').pop();
      const typedUri = `${tempUri}.${ext}`;
      await FileSystem.moveAsync({ from: tempUri, to: typedUri });
      
      if (Platform.OS === 'android') {
        const contentUri = await FileSystem.getContentUriAsync(typedUri);
        await IntentLauncher.startActivityAsync('android.intent.action.VIEW', {
          data: contentUri, flags: 1, type: entry.mimeType || '*/*'
        });
      } else {
        await Sharing.shareAsync(typedUri);
      }
    } catch (e) {
      console.error(e);
      toast.error('Decryption Failed', 'Could not open file');
    }
  };

  const shareFile = async (entry: VaultEntry) => {
    try {
      const VAULT_DIR = `${FileSystem.documentDirectory}vault/`;
      const tempUri = await decryptFile(`${VAULT_DIR}${entry.encryptedFilename}`, entry.iv);
      const ext = entry.originalName.split('.').pop();
      const typedUri = `${tempUri}_share.${ext}`;
      await FileSystem.moveAsync({ from: tempUri, to: typedUri });
      
      await Sharing.shareAsync(typedUri);
    } catch (e) {
      toast.error('Share Failed', 'Could not share file');
    }
  };

  const moveFile = async (entryId: string, newCategoryId: string) => {
    if (!manifest) return;
    const updatedEntries = manifest.entries.map(e => e.id === entryId ? { ...e, categoryId: newCategoryId } : e);
    await saveManifest({ ...manifest, entries: updatedEntries });
    toast.success('Moved', 'File category updated');
  };

  const addCategory = (name: string, icon: string, color: string) => {
    if (!manifest) return;
    const newCat: VaultCategory = {
      id: `cat_${Date.now()}`,
      name, icon, color, fileCount: 0
    };
    saveManifest({ ...manifest, categories: [...manifest.categories, newCat] });
  };

  const removeCategory = (categoryId: string) => {
    if (!manifest) return;
    if (manifest.entries.some(e => e.categoryId === categoryId)) {
      toast.error('Error', 'Category must be empty to delete');
      return;
    }
    saveManifest({ ...manifest, categories: manifest.categories.filter(c => c.id !== categoryId) });
  };

  const getEntriesForCategory = (categoryId: string): VaultEntry[] => {
    if (!manifest) return [];
    return manifest.entries.filter(e => e.categoryId === categoryId).sort((a,b) => b.dateAdded - a.dateAdded);
  };

  const searchEntries = (query: string): VaultEntry[] => {
    if (!manifest || !query.trim()) return [];
    return manifest.entries.filter(e => fuzzyIsMatch(query, e.originalName));
  };

  return {
    manifest,
    isLoading,
    addFile,
    removeFile,
    openFile,
    shareFile,
    moveFile,
    addCategory,
    removeCategory,
    getEntriesForCategory,
    searchEntries
  };
};
