import { useState, useEffect } from 'react';
import { VaultManifest, VaultEntry, VaultCategory } from './vaultTypes';
import { encryptFile, decryptFile, cleanupTempFiles, ensureVaultDirs } from './vaultCrypto';
import EncryptedStorage from '../../utils/EncryptedStorage';
import AsyncStorage from '@react-native-async-storage/async-storage';
import * as FileSystem from 'expo-file-system/legacy';
import * as IntentLauncher from 'expo-intent-launcher';
import * as Sharing from 'expo-sharing';
import { Platform } from 'react-native';
import { toast } from '../../context/ToastContext';
import { fuzzyIsMatch } from '../../utils/textNormalize';

const MANIFEST_KEY = '@flyshelf_vault_manifest';
const VAULT_DIR = `${FileSystem.documentDirectory}vault/`;
const DISK_MANIFEST_PATH = `${FileSystem.documentDirectory}vault_manifest_backup.json`;

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

  const saveManifest = async (newManifest: VaultManifest) => {
    try {
      newManifest.lastModified = Date.now();
      const counts = newManifest.entries.reduce((acc: any, e: any) => {
        acc[e.categoryId] = (acc[e.categoryId] || 0) + 1;
        return acc;
      }, {});
      newManifest.categories.forEach((c: any) => c.fileCount = counts[c.id] || 0);

      const json = JSON.stringify(newManifest);
      // Layer 1: AsyncStorage
      await AsyncStorage.setItem(MANIFEST_KEY, json).catch(() => {});
      // Layer 2: EncryptedStorage
      await EncryptedStorage.setItem(MANIFEST_KEY, json).catch(() => {});
      // Layer 3: Direct JSON file on disk (immune to cache/update resets)
      await FileSystem.writeAsStringAsync(DISK_MANIFEST_PATH, json).catch(() => {});

      setManifest(newManifest);
    } catch (e) {
      console.error('Failed to save vault manifest', e);
      toast.error('Save Error', 'Could not save storage changes');
    }
  };

  const loadManifest = async () => {
    try {
      await ensureVaultDirs();
      let rawData: string | null = null;

      // Level 1: AsyncStorage
      try {
        rawData = await AsyncStorage.getItem(MANIFEST_KEY);
      } catch {}

      // Level 2: EncryptedStorage
      if (!rawData) {
        try {
          rawData = await EncryptedStorage.getItem(MANIFEST_KEY);
        } catch {}
      }

      // Level 3: Disk backup file
      if (!rawData) {
        try {
          const info = await FileSystem.getInfoAsync(DISK_MANIFEST_PATH);
          if (info.exists) {
            rawData = await FileSystem.readAsStringAsync(DISK_MANIFEST_PATH);
          }
        } catch {}
      }

      let parsed: VaultManifest;
      if (rawData) {
        try {
          parsed = JSON.parse(rawData);
          if (!parsed.categories || !Array.isArray(parsed.categories)) {
            parsed.categories = DEFAULT_CATEGORIES;
          }
          if (!parsed.entries || !Array.isArray(parsed.entries)) {
            parsed.entries = [];
          }
        } catch {
          parsed = {
            version: 1,
            categories: DEFAULT_CATEGORIES,
            entries: [],
            lastModified: Date.now()
          };
        }
      } else {
        parsed = {
          version: 1,
          categories: DEFAULT_CATEGORIES,
          entries: [],
          lastModified: Date.now()
        };
      }

      // ── Self-Healing Auto-Recovery Scanner ──
      // Scan vault directory on disk to recover any orphaned or un-manifested files
      try {
        const dirFiles = await FileSystem.readDirectoryAsync(VAULT_DIR);
        const manifestFilenames = new Set(parsed.entries.map(e => e.encryptedFilename));
        let recoveredCount = 0;

        for (const file of dirFiles) {
          if (!manifestFilenames.has(file)) {
            const filePath = `${VAULT_DIR}${file}`;
            const fileInfo = await FileSystem.getInfoAsync(filePath);
            const ext = file.split('.').pop()?.toLowerCase() || '';
            let mimeType = 'application/octet-stream';
            let catId = 'cat_docs';

            if (['pdf'].includes(ext)) {
              mimeType = 'application/pdf';
              catId = 'cat_docs';
            } else if (['jpg', 'jpeg', 'png', 'webp', 'gif'].includes(ext)) {
              mimeType = `image/${ext === 'jpg' ? 'jpeg' : ext}`;
              catId = 'cat_personal';
            } else if (['mp4', 'mkv', 'mov', 'avi'].includes(ext)) {
              mimeType = 'video/mp4';
              catId = 'cat_personal';
            } else if (['doc', 'docx', 'txt', 'xlsx', 'pptx'].includes(ext)) {
              mimeType = 'application/octet-stream';
              catId = 'cat_docs';
            }

            const cleanName = file.replace(/^[a-z0-9_]+__/, '');

            const recoveredEntry: VaultEntry = {
              id: `ve_rec_${Date.now()}_${Math.random().toString(36).substr(2, 6)}`,
              originalName: cleanName || file,
              encryptedFilename: file,
              mimeType,
              fileSize: (fileInfo.exists && 'size' in fileInfo && typeof fileInfo.size === 'number') ? fileInfo.size : 0,
              categoryId: catId,
              dateAdded: (fileInfo.exists && 'modificationTime' in fileInfo && typeof fileInfo.modificationTime === 'number') ? fileInfo.modificationTime * 1000 : Date.now(),
            };
            parsed.entries.push(recoveredEntry);
            manifestFilenames.add(file);
            recoveredCount++;
          }
        }

        if (recoveredCount > 0) {
          console.log(`[Vault] Reconciled ${recoveredCount} orphaned files!`);
        }
      } catch (scanErr) {
        console.warn('[Vault] Directory reconciliation check:', scanErr);
      }

      // Re-sync category file counts
      const counts = parsed.entries.reduce((acc: any, e: any) => {
        acc[e.categoryId] = (acc[e.categoryId] || 0) + 1;
        return acc;
      }, {});
      parsed.categories.forEach((c: any) => c.fileCount = counts[c.id] || 0);

      // Save to all layers
      await saveManifest(parsed);
      setManifest(parsed);
    } catch (e) {
      console.error('Failed to load storage manifest', e);
      toast.error('Storage Error', 'Could not load storage data');
    } finally {
      setIsLoading(false);
    }
  };

  useEffect(() => {
    loadManifest();
    return () => { cleanupTempFiles(); };
  }, []);

  const addFile = async (sourceUri: string, fileName: string, mimeType: string, categoryId: string, fileSize: number = 0) => {
    if (!manifest) return;
    try {
      await ensureVaultDirs();
      const safeId = `ve_${Date.now()}_${Math.random().toString(36).substr(2, 6)}`;
      const cleanName = fileName.replace(/[^a-zA-Z0-9._-]/g, '_');
      const targetFilename = `${safeId}__${cleanName}`;
      const targetUri = `${VAULT_DIR}${targetFilename}`;

      // Store file directly — NO encryption lockout risk on app updates!
      await FileSystem.copyAsync({ from: sourceUri, to: targetUri });

      let actualSize = fileSize;
      if (!actualSize) {
        try {
          const info = await FileSystem.getInfoAsync(targetUri);
          if (info.exists && 'size' in info && typeof info.size === 'number') {
            actualSize = info.size;
          }
        } catch {}
      }

      const newEntry: VaultEntry = {
        id: safeId,
        originalName: fileName,
        encryptedFilename: targetFilename,
        mimeType,
        fileSize: actualSize,
        categoryId,
        dateAdded: Date.now(),
      };

      setManifest(prev => {
        if (!prev) return prev;
        const updated = { ...prev, entries: [newEntry, ...prev.entries] };
        saveManifest(updated).catch(() => {});
        return updated;
      });
      toast.success('File Saved', 'Added to offline storage');
    } catch (e: any) {
      console.error('Failed to add file to storage:', e);
      toast.error('Save Failed', e?.message || 'Could not save file');
    }
  };

  const removeFile = async (entryId: string) => {
    if (!manifest) return;
    const entry = manifest.entries.find(e => e.id === entryId);
    if (!entry) return;
    try {
      await FileSystem.deleteAsync(`${VAULT_DIR}${entry.encryptedFilename}`, { idempotent: true });
      setManifest(prev => {
        if (!prev) return prev;
        const updated = { ...prev, entries: prev.entries.filter(e => e.id !== entryId) };
        saveManifest(updated).catch(() => {});
        return updated;
      });
      toast.success('File Removed', 'File deleted from storage');
    } catch (e) {
      toast.error('Error', 'Could not delete file');
    }
  };

  const resolveFileUri = async (entry: VaultEntry): Promise<string> => {
    const filePath = `${VAULT_DIR}${entry.encryptedFilename}`;
    // If entry has an IV, it's a legacy encrypted file -> decrypt to temp
    if (entry.iv) {
      try {
        const tempUri = await decryptFile(filePath, entry.iv);
        const ext = entry.originalName.split('.').pop() || 'bin';
        const typedUri = `${tempUri}.${ext}`;
        await FileSystem.moveAsync({ from: tempUri, to: typedUri });
        return typedUri;
      } catch (err) {
        console.warn('Legacy decrypt error, attempting direct file:', err);
      }
    }
    return filePath;
  };

  const openFile = async (entry: VaultEntry) => {
    try {
      const uriToOpen = await resolveFileUri(entry);
      if (Platform.OS === 'android') {
        const contentUri = await FileSystem.getContentUriAsync(uriToOpen);
        await IntentLauncher.startActivityAsync('android.intent.action.VIEW', {
          data: contentUri, flags: 1, type: entry.mimeType || '*/*'
        });
      } else {
        await Sharing.shareAsync(uriToOpen);
      }
    } catch (e: any) {
      console.error(e);
      toast.error('Open Failed', e?.message || 'Could not open file');
    }
  };

  const shareFile = async (entry: VaultEntry) => {
    try {
      const uriToShare = await resolveFileUri(entry);
      await Sharing.shareAsync(uriToShare);
    } catch (e: any) {
      toast.error('Share Failed', e?.message || 'Could not share file');
    }
  };

  const getDecryptedFilePath = async (entry: VaultEntry): Promise<string> => {
    return resolveFileUri(entry);
  };

  const moveFile = async (entryId: string, newCategoryId: string) => {
    setManifest(prev => {
      if (!prev) return prev;
      const updatedEntries = prev.entries.map(e => e.id === entryId ? { ...e, categoryId: newCategoryId } : e);
      const updated = { ...prev, entries: updatedEntries };
      saveManifest(updated).catch(() => {});
      return updated;
    });
    toast.success('Moved', 'File category updated');
  };

  const addCategory = (name: string, icon: string, color: string) => {
    setManifest(prev => {
      if (!prev) return prev;
      const newCat: VaultCategory = {
        id: `cat_${Date.now()}`,
        name, icon, color, fileCount: 0
      };
      const updated = { ...prev, categories: [...prev.categories, newCat] };
      saveManifest(updated).catch(() => {});
      return updated;
    });
  };

  const removeCategory = (categoryId: string) => {
    setManifest(prev => {
      if (!prev) return prev;
      if (prev.entries.some(e => e.categoryId === categoryId)) {
        toast.error('Error', 'Category must be empty to delete');
        return prev;
      }
      const updated = { ...prev, categories: prev.categories.filter(c => c.id !== categoryId) };
      saveManifest(updated).catch(() => {});
      return updated;
    });
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
    getDecryptedFilePath,
    moveFile,
    addCategory,
    removeCategory,
    getEntriesForCategory,
    searchEntries
  };
};
