import { useState, useEffect, useRef } from 'react';
import { VaultManifest, VaultEntry, VaultCategory } from './vaultTypes';
import { decryptFile, cleanupTempFiles, ensureVaultDirs } from './vaultCrypto';
import EncryptedStorage from '../../utils/EncryptedStorage';
import AsyncStorage from '@react-native-async-storage/async-storage';
import * as FileSystem from 'expo-file-system/legacy';
import * as IntentLauncher from 'expo-intent-launcher';
import * as Sharing from 'expo-sharing';
import { Platform, NativeModules, AppState } from 'react-native';
import { toast } from '../../context/ToastContext';
import { fuzzyIsMatch } from '../../utils/textNormalize';

const AdvanceOverlay = NativeModules.AdvanceOverlay;

const MANIFEST_KEY = '@flyshelf_vault_manifest';

// INTERNAL fallback (wiped on uninstall but always available)
const INTERNAL_VAULT_DIR = `${FileSystem.documentDirectory}vault/`;
const INTERNAL_MANIFEST_PATH = `${FileSystem.documentDirectory}vault_manifest_backup.json`;

// PRIMARY: /storage/emulated/0/FlyShelf/Vault/ — survives reinstall!
// This path is resolved at runtime via native module
let FLYSHELF_BASE = ''; // /storage/emulated/0/FlyShelf
let FLYSHELF_VAULT_DIR = ''; // /storage/emulated/0/FlyShelf/Vault/
let FLYSHELF_MANIFEST_PATH = ''; // /storage/emulated/0/FlyShelf/Vault/vault_manifest.json
let FLYSHELF_CLIPBOARD_DIR = ''; // /storage/emulated/0/FlyShelf/Clipboard/
let externalReady = false;

/** Initialize the FlyShelf external storage paths */
const initExternalPaths = async (): Promise<boolean> => {
  if (externalReady) return true;
  try {
    if (!AdvanceOverlay?.getFlyShelfStoragePath) return false;
    const basePath: string = await AdvanceOverlay.getFlyShelfStoragePath();
    if (!basePath) return false;
    FLYSHELF_BASE = basePath;
    FLYSHELF_VAULT_DIR = `file://${basePath}/Vault/`;
    FLYSHELF_MANIFEST_PATH = `file://${basePath}/Vault/vault_manifest.json`;
    FLYSHELF_CLIPBOARD_DIR = `file://${basePath}/Clipboard/`;
    // Create the directories via native (uses java.io.File which doesn't need file:// prefix)
    await AdvanceOverlay.ensureFlyShelfDirs();
    externalReady = true;
    return true;
  } catch {
    return false;
  }
};

/** Ensure a directory exists */
const ensureDir = async (dir: string) => {
  try {
    const info = await FileSystem.getInfoAsync(dir);
    if (!info.exists) await FileSystem.makeDirectoryAsync(dir, { intermediates: true });
  } catch {}
};

/** Get the active vault directory (external if available, else internal) */
const getVaultDir = (): string => externalReady ? FLYSHELF_VAULT_DIR : INTERNAL_VAULT_DIR;

const DEFAULT_CATEGORIES: VaultCategory[] = [
  { id: 'cat_docs', name: 'Documents', icon: '📄', color: '#60A5FA', fileCount: 0 },
  { id: 'cat_ids', name: 'IDs & Cards', icon: '🆔', color: '#FBBF24', fileCount: 0 },
  { id: 'cat_finance', name: 'Finance', icon: '💰', color: '#34D399', fileCount: 0 },
  { id: 'cat_health', name: 'Health', icon: '🏥', color: '#F87171', fileCount: 0 },
  { id: 'cat_education', name: 'Education', icon: '📚', color: '#A78BFA', fileCount: 0 },
  { id: 'cat_personal', name: 'Personal', icon: '🔒', color: '#8B92A0', fileCount: 0 }
];

/** Save manifest to ALL storage layers */
const persistManifest = async (json: string) => {
  // Layer 1: AsyncStorage
  await AsyncStorage.setItem(MANIFEST_KEY, json).catch(() => {});
  // Layer 2: EncryptedStorage
  await EncryptedStorage.setItem(MANIFEST_KEY, json).catch(() => {});
  // Layer 3: Internal disk
  await FileSystem.writeAsStringAsync(INTERNAL_MANIFEST_PATH, json).catch(() => {});
  // Layer 4: External FlyShelf folder (survives reinstall)
  if (externalReady) {
    await FileSystem.writeAsStringAsync(FLYSHELF_MANIFEST_PATH, json).catch(() => {});
  }
};

/** Load manifest from best available source */
const loadManifestData = async (): Promise<string | null> => {
  let rawData: string | null = null;

  // 1. AsyncStorage
  try { rawData = await AsyncStorage.getItem(MANIFEST_KEY); } catch {}

  // 2. EncryptedStorage
  if (!rawData) {
    try { rawData = await EncryptedStorage.getItem(MANIFEST_KEY); } catch {}
  }

  // 3. Internal disk
  if (!rawData) {
    try {
      const info = await FileSystem.getInfoAsync(INTERNAL_MANIFEST_PATH);
      if (info.exists) rawData = await FileSystem.readAsStringAsync(INTERNAL_MANIFEST_PATH);
    } catch {}
  }

  // 4. External FlyShelf folder (critical after reinstall!)
  if (!rawData && externalReady) {
    try {
      const info = await FileSystem.getInfoAsync(FLYSHELF_MANIFEST_PATH);
      if (info.exists) {
        rawData = await FileSystem.readAsStringAsync(FLYSHELF_MANIFEST_PATH);
        if (rawData) {
          console.log('[Vault] ✅ Recovered manifest from /storage/emulated/0/FlyShelf/Vault/');
          toast.success('Vault Recovered', 'Your vault data was restored after reinstall');
        }
      }
    } catch {}
  }

  return rawData;
};

/** Copy file to both internal and external vault */
const storeFile = async (sourceUri: string, filename: string): Promise<string> => {
  const vaultDir = getVaultDir();
  await ensureDir(vaultDir);
  const targetUri = `${vaultDir}${filename}`;
  await FileSystem.copyAsync({ from: sourceUri, to: targetUri });

  // Also mirror to the other location
  if (externalReady && vaultDir === FLYSHELF_VAULT_DIR) {
    // Already in external, also copy to internal as backup
    await ensureDir(INTERNAL_VAULT_DIR);
    await FileSystem.copyAsync({ from: targetUri, to: `${INTERNAL_VAULT_DIR}${filename}` }).catch(() => {});
  } else if (externalReady) {
    // In internal, also copy to external
    await FileSystem.copyAsync({ from: targetUri, to: `${FLYSHELF_VAULT_DIR}${filename}` }).catch(() => {});
  }

  return targetUri;
};

/** Find a file across all vault locations */
const resolveFile = async (filename: string): Promise<string> => {
  const locations = [
    `${getVaultDir()}${filename}`,
    externalReady ? `${FLYSHELF_VAULT_DIR}${filename}` : '',
    `${INTERNAL_VAULT_DIR}${filename}`,
  ].filter(Boolean);

  for (const loc of locations) {
    try {
      const info = await FileSystem.getInfoAsync(loc);
      if (info.exists && (info as any).size > 0) return loc;
    } catch {}
  }

  // Return primary location even if not found (will error gracefully)
  return `${getVaultDir()}${filename}`;
};

/** Delete file from ALL locations permanently */
const deleteFileEverywhere = async (filename: string) => {
  const locations = [
    `${INTERNAL_VAULT_DIR}${filename}`,
    externalReady ? `${FLYSHELF_VAULT_DIR}${filename}` : '',
  ].filter(Boolean);

  for (const loc of locations) {
    await FileSystem.deleteAsync(loc, { idempotent: true }).catch(() => {});
  }
};

/** Scan a directory for vault files not in the manifest */
const scanDirForOrphans = async (dir: string, knownFiles: Set<string>): Promise<VaultEntry[]> => {
  const recovered: VaultEntry[] = [];
  try {
    const dirInfo = await FileSystem.getInfoAsync(dir);
    if (!dirInfo.exists) return recovered;
    const files = await FileSystem.readDirectoryAsync(dir);

    for (const file of files) {
      if (file.endsWith('.json') || knownFiles.has(file)) continue;
      const filePath = `${dir}${file}`;
      const fileInfo = await FileSystem.getInfoAsync(filePath);
      if (!fileInfo.exists || (fileInfo as any).size === 0) continue;

      const ext = file.split('.').pop()?.toLowerCase() || '';
      let mimeType = 'application/octet-stream';
      let catId = 'cat_docs';
      if (['pdf'].includes(ext)) { mimeType = 'application/pdf'; catId = 'cat_docs'; }
      else if (['jpg', 'jpeg', 'png', 'webp', 'gif'].includes(ext)) { mimeType = `image/${ext === 'jpg' ? 'jpeg' : ext}`; catId = 'cat_personal'; }
      else if (['mp4', 'mkv', 'mov', 'avi'].includes(ext)) { mimeType = 'video/mp4'; catId = 'cat_personal'; }
      else if (['doc', 'docx', 'txt', 'xlsx', 'pptx'].includes(ext)) { catId = 'cat_docs'; }

      const cleanName = file.replace(/^[a-z0-9_]+__/, '');
      recovered.push({
        id: `ve_rec_${Date.now()}_${Math.random().toString(36).substr(2, 6)}`,
        originalName: cleanName || file,
        encryptedFilename: file,
        mimeType,
        fileSize: ('size' in fileInfo && typeof fileInfo.size === 'number') ? fileInfo.size : 0,
        categoryId: catId,
        dateAdded: ('modificationTime' in fileInfo && typeof fileInfo.modificationTime === 'number') ? fileInfo.modificationTime * 1000 : Date.now(),
        origin: 'Recovered',
      });
      knownFiles.add(file);
    }
  } catch {}
  return recovered;
};

export const useVault = () => {
  const [manifest, setManifest] = useState<VaultManifest | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [hasPermission, setHasPermission] = useState(true);
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
      await persistManifest(json);
      manifestRef.current = newManifest;
      setManifest(newManifest);
    } catch (e) {
      console.error('Failed to save vault manifest', e);
      toast.error('Save Error', 'Could not save storage changes');
    }
  };

  const loadManifest = async () => {
    try {
      // Init external storage paths
      await initExternalPaths();

      // Check permission
      if (AdvanceOverlay?.hasAllFilesPermission) {
        try {
          const hasPerm = await AdvanceOverlay.hasAllFilesPermission();
          setHasPermission(hasPerm);
        } catch {}
      }

      await ensureVaultDirs();
      await ensureDir(INTERNAL_VAULT_DIR);
      if (externalReady) await ensureDir(FLYSHELF_VAULT_DIR);

      const rawData = await loadManifestData();

      let parsed: VaultManifest;
      if (rawData) {
        try {
          parsed = JSON.parse(rawData);
          if (!parsed.categories || !Array.isArray(parsed.categories)) parsed.categories = DEFAULT_CATEGORIES;
          if (!parsed.entries || !Array.isArray(parsed.entries)) parsed.entries = [];
        } catch {
          parsed = { version: 1, categories: DEFAULT_CATEGORIES, entries: [], lastModified: Date.now() };
        }
      } else {
        parsed = { version: 1, categories: DEFAULT_CATEGORIES, entries: [], lastModified: Date.now() };
      }

      // Restore files: if primary vault is missing files, try copying from the other location
      if (parsed.entries.length > 0) {
        let restored = 0;
        for (const entry of parsed.entries) {
          const primaryPath = `${getVaultDir()}${entry.encryptedFilename}`;
          try {
            const info = await FileSystem.getInfoAsync(primaryPath);
            if (info.exists && (info as any).size > 0) continue;
          } catch {}

          // Try other locations
          const fallbacks = [
            `${INTERNAL_VAULT_DIR}${entry.encryptedFilename}`,
            externalReady ? `${FLYSHELF_VAULT_DIR}${entry.encryptedFilename}` : '',
          ].filter(Boolean);
          for (const fb of fallbacks) {
            try {
              const fbInfo = await FileSystem.getInfoAsync(fb);
              if (fbInfo.exists && (fbInfo as any).size > 0) {
                await FileSystem.copyAsync({ from: fb, to: primaryPath });
                restored++;
                break;
              }
            } catch {}
          }
        }
        if (restored > 0) {
          toast.success('Files Restored', `${restored} vault files recovered`);
        }
      }

      // Self-healing: scan all vault directories for orphaned files
      const knownFiles = new Set(parsed.entries.map(e => e.encryptedFilename));
      const scanDirs = [INTERNAL_VAULT_DIR];
      if (externalReady) scanDirs.push(FLYSHELF_VAULT_DIR);

      for (const dir of scanDirs) {
        const orphans = await scanDirForOrphans(dir, knownFiles);
        if (orphans.length > 0) {
          parsed.entries.push(...orphans);
          console.log(`[Vault] Recovered ${orphans.length} orphaned files from ${dir}`);
        }
      }

      // Sync counts and save
      const counts = parsed.entries.reduce((acc: any, e: any) => {
        acc[e.categoryId] = (acc[e.categoryId] || 0) + 1;
        return acc;
      }, {});
      parsed.categories.forEach((c: any) => c.fileCount = counts[c.id] || 0);
      await saveManifest(parsed);
    } catch (e) {
      console.error('Failed to load vault manifest', e);
      toast.error('Storage Error', 'Could not load vault data');
    } finally {
      setIsLoading(false);
    }
  };

  // Re-check permission when app comes to foreground (user may have just granted it)
  useEffect(() => {
    const sub = AppState.addEventListener('change', async (state) => {
      if (state === 'active' && AdvanceOverlay?.hasAllFilesPermission) {
        try {
          const hasPerm = await AdvanceOverlay.hasAllFilesPermission();
          setHasPermission(hasPerm);
          if (hasPerm && !externalReady) {
            await initExternalPaths();
          }
        } catch {}
      }
    });
    return () => sub.remove();
  }, []);

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
      const safeId = `ve_${Date.now()}_${Math.random().toString(36).substr(2, 6)}`;
      const cleanName = fileName.replace(/[^a-zA-Z0-9._-]/g, '_');
      const targetFilename = `${safeId}__${cleanName}`;

      await storeFile(sourceUri, targetFilename);

      let actualSize = fileSize;
      if (!actualSize) {
        try {
          const filePath = `${getVaultDir()}${targetFilename}`;
          const info = await FileSystem.getInfoAsync(filePath);
          if (info.exists && 'size' in info && typeof info.size === 'number') actualSize = info.size;
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
        origin: origin || 'Phone',
        originDevice: originDevice || undefined,
      };

      setManifest(prev => {
        if (!prev) return prev;
        const updated = { ...prev, entries: [newEntry, ...prev.entries] };
        saveManifest(updated).catch(() => {});
        return updated;
      });
      toast.success('File Saved', 'Added to FlyShelf storage');
    } catch (e: any) {
      console.error('Failed to add file to vault:', e);
      toast.error('Save Failed', e?.message || 'Could not save file');
    }
  };

  const removeFile = async (entryId: string) => {
    const m = manifestRef.current;
    if (!m) return;
    const entry = m.entries.find(e => e.id === entryId);
    if (!entry) return;
    try {
      await deleteFileEverywhere(entry.encryptedFilename);
      setManifest(prev => {
        if (!prev) return prev;
        const updated = { ...prev, entries: prev.entries.filter(e => e.id !== entryId) };
        saveManifest(updated).catch(() => {});
        return updated;
      });
      toast.success('Permanently Deleted', 'File removed from all storage');
    } catch (e) {
      toast.error('Error', 'Could not delete file');
    }
  };

  const openFile = async (entry: VaultEntry) => {
    try {
      const uriToOpen = await resolveFile(entry.encryptedFilename);
      // Handle legacy encrypted files
      if (entry.iv) {
        try {
          const tempUri = await decryptFile(uriToOpen, entry.iv);
          const ext = entry.originalName.split('.').pop() || 'bin';
          const typedUri = `${tempUri}.${ext}`;
          await FileSystem.moveAsync({ from: tempUri, to: typedUri });
          if (Platform.OS === 'android') {
            const contentUri = await FileSystem.getContentUriAsync(typedUri);
            await IntentLauncher.startActivityAsync('android.intent.action.VIEW', {
              data: contentUri, flags: 1, type: entry.mimeType || '*/*'
            });
          }
          return;
        } catch {}
      }
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
      const uriToShare = await resolveFile(entry.encryptedFilename);
      await Sharing.shareAsync(uriToShare);
    } catch (e: any) {
      toast.error('Share Failed', e?.message || 'Could not share file');
    }
  };

  const getDecryptedFilePath = async (entry: VaultEntry): Promise<string> => {
    return resolveFile(entry.encryptedFilename);
  };

  const moveFile = async (entryId: string, newCategoryId: string) => {
    setManifest(prev => {
      if (!prev) return prev;
      const updated = { ...prev, entries: prev.entries.map(e => e.id === entryId ? { ...e, categoryId: newCategoryId } : e) };
      saveManifest(updated).catch(() => {});
      return updated;
    });
    toast.success('Moved', 'File category updated');
  };

  const addCategory = (name: string, icon: string, color: string) => {
    setManifest(prev => {
      if (!prev) return prev;
      const newCat: VaultCategory = { id: `cat_${Date.now()}`, name, icon, color, fileCount: 0 };
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

  const requestPermission = () => {
    if (AdvanceOverlay?.requestAllFilesPermission) {
      AdvanceOverlay.requestAllFilesPermission();
    }
  };

  return {
    manifest,
    isLoading,
    hasPermission,
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
    requestPermission,
    storagePath: externalReady ? FLYSHELF_BASE : 'Internal',
  };
};
