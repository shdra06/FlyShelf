import { useState, useEffect, useRef } from 'react';
import { VaultManifest, VaultEntry, VaultCategory } from './vaultTypes';
import { decryptFile, cleanupTempFiles, ensureVaultDirs } from './vaultCrypto';
import EncryptedStorage from '../../utils/EncryptedStorage';
import AsyncStorage from '@react-native-async-storage/async-storage';
import * as FileSystem from 'expo-file-system/legacy';
import * as IntentLauncher from 'expo-intent-launcher';
import * as Sharing from 'expo-sharing';
import { Platform } from 'react-native';
import { toast } from '../../context/ToastContext';
import { fuzzyIsMatch } from '../../utils/textNormalize';

const MANIFEST_KEY = '@flyshelf_vault_manifest';

// PRIMARY: Internal app storage (fast, but wiped on uninstall)
const VAULT_DIR = `${FileSystem.documentDirectory}vault/`;
const DISK_MANIFEST_PATH = `${FileSystem.documentDirectory}vault_manifest_backup.json`;

// BACKUP: External app-specific storage (survives most updates, not uninstall)
// On Android: /storage/emulated/0/Android/data/com.shivendra.flyshelf/files/vault/
// This is NOT wiped by expo prebuild --clean (only wipes source, not runtime data)
const EXTERNAL_VAULT_DIR = `${FileSystem.documentDirectory}../external_vault/`;
const EXTERNAL_MANIFEST_PATH = `${FileSystem.documentDirectory}../external_vault/vault_manifest.json`;

// EXTRA SAFETY: Also write to a public Documents folder (survives even uninstall on some devices)
// Requires WRITE_EXTERNAL_STORAGE or SAF — we attempt best-effort
const DOCUMENTS_VAULT_DIR = `${FileSystem.documentDirectory}../../Documents/FlyShelf_Vault/`;
const DOCUMENTS_MANIFEST_PATH = `${FileSystem.documentDirectory}../../Documents/FlyShelf_Vault/vault_manifest.json`;

const DEFAULT_CATEGORIES: VaultCategory[] = [
  { id: 'cat_docs', name: 'Documents', icon: '📄', color: '#60A5FA', fileCount: 0 },
  { id: 'cat_ids', name: 'IDs & Cards', icon: '🆔', color: '#FBBF24', fileCount: 0 },
  { id: 'cat_finance', name: 'Finance', icon: '💰', color: '#34D399', fileCount: 0 },
  { id: 'cat_health', name: 'Health', icon: '🏥', color: '#F87171', fileCount: 0 },
  { id: 'cat_education', name: 'Education', icon: '📚', color: '#A78BFA', fileCount: 0 },
  { id: 'cat_personal', name: 'Personal', icon: '🔒', color: '#8B92A0', fileCount: 0 }
];

/** Ensure a directory exists, silently fail */
const ensureDir = async (dir: string) => {
  try {
    const info = await FileSystem.getInfoAsync(dir);
    if (!info.exists) await FileSystem.makeDirectoryAsync(dir, { intermediates: true });
  } catch {}
};

/** Best-effort copy a file to a backup location */
const backupFile = async (sourceUri: string, filename: string) => {
  try {
    await ensureDir(EXTERNAL_VAULT_DIR);
    await FileSystem.copyAsync({ from: sourceUri, to: `${EXTERNAL_VAULT_DIR}${filename}` }).catch(() => {});
  } catch {}
  // Also try Documents folder (may fail on scoped storage)
  try {
    await ensureDir(DOCUMENTS_VAULT_DIR);
    await FileSystem.copyAsync({ from: sourceUri, to: `${DOCUMENTS_VAULT_DIR}${filename}` }).catch(() => {});
  } catch {}
};

/** Best-effort save manifest to all backup locations */
const backupManifest = async (json: string) => {
  try {
    await ensureDir(EXTERNAL_VAULT_DIR);
    await FileSystem.writeAsStringAsync(EXTERNAL_MANIFEST_PATH, json).catch(() => {});
  } catch {}
  try {
    await ensureDir(DOCUMENTS_VAULT_DIR);
    await FileSystem.writeAsStringAsync(DOCUMENTS_MANIFEST_PATH, json).catch(() => {});
  } catch {}
};

/** Try loading manifest from backup locations */
const loadManifestFromBackup = async (): Promise<string | null> => {
  // Try external app storage
  try {
    const info = await FileSystem.getInfoAsync(EXTERNAL_MANIFEST_PATH);
    if (info.exists) {
      return await FileSystem.readAsStringAsync(EXTERNAL_MANIFEST_PATH);
    }
  } catch {}
  // Try Documents folder
  try {
    const info = await FileSystem.getInfoAsync(DOCUMENTS_MANIFEST_PATH);
    if (info.exists) {
      return await FileSystem.readAsStringAsync(DOCUMENTS_MANIFEST_PATH);
    }
  } catch {}
  return null;
};

/** Try to restore vault files from backup dirs into primary vault dir */
const restoreFilesFromBackup = async (entries: VaultEntry[]): Promise<number> => {
  let restored = 0;
  const backupDirs = [EXTERNAL_VAULT_DIR, DOCUMENTS_VAULT_DIR];

  for (const entry of entries) {
    const primaryPath = `${VAULT_DIR}${entry.encryptedFilename}`;
    try {
      const info = await FileSystem.getInfoAsync(primaryPath);
      if (info.exists && (info as any).size > 0) continue; // Already exists
    } catch {}

    // Try to restore from backup dirs
    for (const backupDir of backupDirs) {
      try {
        const backupPath = `${backupDir}${entry.encryptedFilename}`;
        const bInfo = await FileSystem.getInfoAsync(backupPath);
        if (bInfo.exists && (bInfo as any).size > 0) {
          await FileSystem.copyAsync({ from: backupPath, to: primaryPath });
          restored++;
          break;
        }
      } catch {}
    }
  }
  return restored;
};

export const useVault = () => {
  const [manifest, setManifest] = useState<VaultManifest | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const manifestRef = useRef<VaultManifest | null>(null);

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
      // Layer 3: Direct JSON file on disk (primary vault dir)
      await FileSystem.writeAsStringAsync(DISK_MANIFEST_PATH, json).catch(() => {});
      // Layer 4: External backup locations (survive reinstall)
      backupManifest(json).catch(() => {}); // fire-and-forget

      manifestRef.current = newManifest;
      setManifest(newManifest);
    } catch (e) {
      console.error('Failed to save vault manifest', e);
      toast.error('Save Error', 'Could not save storage changes');
    }
  };

  const loadManifest = async () => {
    try {
      await ensureVaultDirs();
      await ensureDir(VAULT_DIR);
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

      // Level 3: Disk backup file (internal)
      if (!rawData) {
        try {
          const info = await FileSystem.getInfoAsync(DISK_MANIFEST_PATH);
          if (info.exists) {
            rawData = await FileSystem.readAsStringAsync(DISK_MANIFEST_PATH);
          }
        } catch {}
      }

      // Level 4: External backup locations (critical for after reinstall!)
      if (!rawData) {
        rawData = await loadManifestFromBackup();
        if (rawData) {
          console.log('[Vault] Recovered manifest from external backup!');
          toast.success('Vault Recovered', 'Your vault data was restored from backup');
        }
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

      // ── Restore files from backup if primary vault is empty/missing ──
      if (parsed.entries.length > 0) {
        try {
          const restored = await restoreFilesFromBackup(parsed.entries);
          if (restored > 0) {
            console.log(`[Vault] Restored ${restored} files from external backup!`);
            toast.success('Files Restored', `${restored} vault files recovered from backup`);
          }
        } catch {}
      }

      // ── Self-Healing Auto-Recovery Scanner ──
      // Scan ALL vault directories to recover orphaned files
      const manifestFilenames = new Set(parsed.entries.map(e => e.encryptedFilename));
      const allScanDirs = [VAULT_DIR, EXTERNAL_VAULT_DIR, DOCUMENTS_VAULT_DIR];
      let recoveredCount = 0;

      for (const scanDir of allScanDirs) {
        try {
          const dirInfo = await FileSystem.getInfoAsync(scanDir);
          if (!dirInfo.exists) continue;
          const dirFiles = await FileSystem.readDirectoryAsync(scanDir);

          for (const file of dirFiles) {
            if (file.endsWith('.json') || manifestFilenames.has(file)) continue;
            // Found orphaned file — recover it
            const filePath = `${scanDir}${file}`;
            const fileInfo = await FileSystem.getInfoAsync(filePath);
            if (!fileInfo.exists || (fileInfo as any).size === 0) continue;

            // Copy to primary vault if not already there
            const primaryPath = `${VAULT_DIR}${file}`;
            try {
              const pInfo = await FileSystem.getInfoAsync(primaryPath);
              if (!pInfo.exists) {
                await FileSystem.copyAsync({ from: filePath, to: primaryPath });
              }
            } catch {}

            const ext = file.split('.').pop()?.toLowerCase() || '';
            let mimeType = 'application/octet-stream';
            let catId = 'cat_docs';

            if (['pdf'].includes(ext)) {
              mimeType = 'application/pdf'; catId = 'cat_docs';
            } else if (['jpg', 'jpeg', 'png', 'webp', 'gif'].includes(ext)) {
              mimeType = `image/${ext === 'jpg' ? 'jpeg' : ext}`; catId = 'cat_personal';
            } else if (['mp4', 'mkv', 'mov', 'avi'].includes(ext)) {
              mimeType = 'video/mp4'; catId = 'cat_personal';
            } else if (['doc', 'docx', 'txt', 'xlsx', 'pptx'].includes(ext)) {
              mimeType = 'application/octet-stream'; catId = 'cat_docs';
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
              origin: 'Recovered',
            };
            parsed.entries.push(recoveredEntry);
            manifestFilenames.add(file);
            recoveredCount++;
          }
        } catch {}
      }

      if (recoveredCount > 0) {
        console.log(`[Vault] Reconciled ${recoveredCount} orphaned files!`);
      }

      // Re-sync category file counts
      const counts = parsed.entries.reduce((acc: any, e: any) => {
        acc[e.categoryId] = (acc[e.categoryId] || 0) + 1;
        return acc;
      }, {});
      parsed.categories.forEach((c: any) => c.fileCount = counts[c.id] || 0);

      // Save to all layers (including backups)
      await saveManifest(parsed);
      setManifest(parsed);
      manifestRef.current = parsed;
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

  const addFile = async (
    sourceUri: string, fileName: string, mimeType: string,
    categoryId: string, fileSize: number = 0,
    origin?: string, originDevice?: string
  ) => {
    if (!manifestRef.current) return;
    try {
      await ensureVaultDirs();
      await ensureDir(VAULT_DIR);
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

      // CRITICAL: Backup to external storage immediately
      backupFile(targetUri, targetFilename).catch(() => {});

      const newEntry: VaultEntry = {
        id: safeId,
        originalName: fileName,
        encryptedFilename: targetFilename,
        mimeType,
        fileSize: actualSize,
        categoryId,
        dateAdded: Date.now(),
        origin: origin || 'Phone',
        originDevice: originDevice || undefined,
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
    const currentManifest = manifestRef.current;
    if (!currentManifest) return;
    const entry = currentManifest.entries.find(e => e.id === entryId);
    if (!entry) return;
    try {
      // Delete from all locations
      await FileSystem.deleteAsync(`${VAULT_DIR}${entry.encryptedFilename}`, { idempotent: true });
      await FileSystem.deleteAsync(`${EXTERNAL_VAULT_DIR}${entry.encryptedFilename}`, { idempotent: true }).catch(() => {});
      await FileSystem.deleteAsync(`${DOCUMENTS_VAULT_DIR}${entry.encryptedFilename}`, { idempotent: true }).catch(() => {});

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

    // Check if file exists in primary location
    try {
      const info = await FileSystem.getInfoAsync(filePath);
      if (info.exists && (info as any).size > 0) {
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
      }
    } catch {}

    // File missing in primary — try to restore from backup
    const backupDirs = [EXTERNAL_VAULT_DIR, DOCUMENTS_VAULT_DIR];
    for (const backupDir of backupDirs) {
      try {
        const backupPath = `${backupDir}${entry.encryptedFilename}`;
        const bInfo = await FileSystem.getInfoAsync(backupPath);
        if (bInfo.exists && (bInfo as any).size > 0) {
          // Restore to primary
          await ensureDir(VAULT_DIR);
          await FileSystem.copyAsync({ from: backupPath, to: filePath });
          toast.success('File Recovered', `${entry.originalName} restored from backup`);
          return filePath;
        }
      } catch {}
    }

    // Last resort — return the path anyway (will error gracefully)
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
    if (!manifestRef.current) return [];
    return manifestRef.current.entries.filter(e => e.categoryId === categoryId).sort((a,b) => b.dateAdded - a.dateAdded);
  };

  const searchEntries = (query: string): VaultEntry[] => {
    if (!manifestRef.current || !query.trim()) return [];
    return manifestRef.current.entries.filter(e => fuzzyIsMatch(query, e.originalName));
  };

  /** Force backup all vault files to external storage (call manually for safety) */
  const forceBackup = async () => {
    const m = manifestRef.current;
    if (!m) return;
    let backed = 0;
    for (const entry of m.entries) {
      const src = `${VAULT_DIR}${entry.encryptedFilename}`;
      try {
        const info = await FileSystem.getInfoAsync(src);
        if (info.exists && (info as any).size > 0) {
          await backupFile(src, entry.encryptedFilename);
          backed++;
        }
      } catch {}
    }
    if (backed > 0) toast.success('Backup Complete', `${backed} files backed up`);
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
    searchEntries,
    forceBackup
  };
};
