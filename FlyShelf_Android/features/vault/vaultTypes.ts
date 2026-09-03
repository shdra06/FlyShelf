export interface VaultCategory {
  id: string;
  name: string;
  icon: string; // emoji
  color: string;
  fileCount: number;
}
export interface VaultEntry {
  id: string;
  originalName: string;
  encryptedFilename: string;
  mimeType: string;
  fileSize: number;
  categoryId: string;
  dateAdded: number;
  iv: string; // encryption IV for this file
  thumbnailBase64?: string; // small thumbnail for images
}
export interface VaultManifest {
  version: number;
  categories: VaultCategory[];
  entries: VaultEntry[];
  lastModified: number;
}
