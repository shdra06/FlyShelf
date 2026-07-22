import * as FileSystem from 'expo-file-system/legacy';

// ═══ ClipItem Type ═══
export type ClipItem = {
  id?: string;
  Title: string;
  Type: string;
  Raw: string;
  Time: string;
  SourceDeviceName?: string;
  SourceDeviceType?: string;
  IsPinned?: boolean;
  Timestamp?: number;
  CachedUri?: string;
  _receivedVia?: 'LAN' | 'Cloud' | 'Local';
  PreviewUrl?: string;
  DownloadUrl?: string;
  EventId?: string;
  Encrypted?: boolean;
};

// ═══ Organized Storage Paths ═══
const _docDir = (FileSystem as any).documentDirectory;
const _cacheDir = (FileSystem as any).cacheDirectory;
if (!_docDir) {
  console.error('[clipTypes] FileSystem.documentDirectory is undefined — file operations will fail');
}
if (!_cacheDir) {
  console.error('[clipTypes] FileSystem.cacheDirectory is undefined — cache operations will fail');
}
export const DOWNLOAD_BASE = `${_docDir || ''}FlyShelf/`;
export const SYNC_CACHE_BASE = `${_cacheDir || ''}FlyShelf/SyncCache/`;
export const CONVERTED_BASE = `${_docDir || ''}FlyShelf/Converted/`;
export const IMAGE_CACHE_BASE = `${_docDir || ''}FlyShelf/Downloads/Images/`;

/** User-initiated downloads: documentDirectory/FlyShelf/Downloads/{subfolder}/{filename} */
export const getDownloadPath = async (subfolder: string, filename: string) => {
  // Sanitize to prevent path traversal
  const safeSubfolder = subfolder.replace(/[^a-zA-Z0-9._-]/g, '_');
  const safeFilename = filename.replace(/[^a-zA-Z0-9._-]/g, '_').replace(/^\.+/, '_');
  if (safeFilename.length === 0 || safeFilename.length > 255) throw new Error('Invalid filename');
  const dir = `${DOWNLOAD_BASE}${safeSubfolder}/`;
  await FileSystem.makeDirectoryAsync(dir, { intermediates: true }).catch(() => {});
  return `${dir}${safeFilename}`;
};

/** Auto-sync temp files: cacheDirectory/FlyShelf/SyncCache/{filename} */
export const getSyncCachePath = async (filename: string) => {
  // Sanitize to prevent path traversal
  const safeFilename = filename.replace(/[^a-zA-Z0-9._-]/g, '_').replace(/^\.+/, '_');
  if (safeFilename.length === 0 || safeFilename.length > 255) throw new Error('Invalid filename');
  await FileSystem.makeDirectoryAsync(SYNC_CACHE_BASE, { intermediates: true }).catch(() => {});
  return `${SYNC_CACHE_BASE}${safeFilename}`;
};

/** Conversion outputs: documentDirectory/FlyShelf/Converted/{filename} */
export const getConvertedPath = async (filename: string) => {
  // Sanitize to prevent path traversal
  const safeFilename = filename.replace(/[^a-zA-Z0-9._-]/g, '_').replace(/^\.+/, '_');
  if (safeFilename.length === 0 || safeFilename.length > 255) throw new Error('Invalid filename');
  await FileSystem.makeDirectoryAsync(CONVERTED_BASE, { intermediates: true }).catch(() => {});
  return `${CONVERTED_BASE}${safeFilename}`;
};

/** Image cache: documentDirectory/FlyShelf/Downloads/Images/{filename} */
export const getImageCachePath = async (filename: string) => {
  await FileSystem.makeDirectoryAsync(IMAGE_CACHE_BASE, { intermediates: true }).catch(() => {});
  return `${IMAGE_CACHE_BASE}${filename}`;
};
