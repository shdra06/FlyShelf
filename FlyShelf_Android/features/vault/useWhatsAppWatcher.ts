import { useState, useEffect, useRef } from 'react';
import { AppState, AppStateStatus } from 'react-native';
import * as FileSystem from 'expo-file-system/legacy';
import AsyncStorage from '@react-native-async-storage/async-storage';

export interface WhatsAppFile {
  uri: string;
  filename: string;
  size: number;
  modifiedTime: number;
  type: 'document' | 'image';
  mimeType: string;
}

const LAST_SCAN_KEY = '@flyshelf_last_wa_scan_ts';

const WA_DIRS = [
  { path: 'file:///storage/emulated/0/WhatsApp/Media/WhatsApp Documents/', type: 'document' as const },
  { path: 'file:///storage/emulated/0/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Documents/', type: 'document' as const },
  { path: 'file:///storage/emulated/0/WhatsApp/Media/WhatsApp Images/', type: 'image' as const },
  { path: 'file:///storage/emulated/0/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/', type: 'image' as const }
];

const ALLOWED_EXTENSIONS = new Set(['pdf', 'doc', 'docx', 'xlsx', 'xls', 'pptx', 'ppt', 'txt', 'csv', 'jpg', 'jpeg', 'png']);

/**
 * Maps common file extensions to their corresponding MIME types.
 */
function getMimeType(ext: string): string {
  const mimes: Record<string, string> = {
    pdf: 'application/pdf',
    doc: 'application/msword',
    docx: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    xls: 'application/vnd.ms-excel',
    xlsx: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    ppt: 'application/vnd.ms-powerpoint',
    pptx: 'application/vnd.openxmlformats-officedocument.presentationml.presentation',
    txt: 'text/plain',
    csv: 'text/csv',
    jpg: 'image/jpeg',
    jpeg: 'image/jpeg',
    png: 'image/png'
  };
  return mimes[ext] || 'application/octet-stream';
}

/**
 * Hook to detect newly received WhatsApp files (documents and images).
 * Scans known WhatsApp media directories on mount and when app comes to foreground.
 */
export function useWhatsAppWatcher(): {
  newFiles: WhatsAppFile[];
  newFileCount: number;
  dismissFiles: () => void;
  isChecking: boolean;
} {
  const [newFiles, setNewFiles] = useState<WhatsAppFile[]>([]);
  const [isChecking, setIsChecking] = useState<boolean>(true);
  const appState = useRef(AppState.currentState);

  const checkFiles = async () => {
    setIsChecking(true);
    try {
      const lastScanStr = await AsyncStorage.getItem(LAST_SCAN_KEY);
      const lastScanTs = lastScanStr ? parseInt(lastScanStr, 10) : 0;
      
      const foundFiles: WhatsAppFile[] = [];

      for (const dir of WA_DIRS) {
        let dirInfo;
        try {
          dirInfo = await FileSystem.getInfoAsync(dir.path);
        } catch (e) {
          // Directory might not be accessible
          continue;
        }

        if (!dirInfo.exists || !dirInfo.isDirectory) {
          // Directory does not exist (e.g., WhatsApp not installed or old/new path discrepancy)
          continue;
        }

        let files: string[] = [];
        try {
          files = await FileSystem.readDirectoryAsync(dir.path);
        } catch (e) {
          continue;
        }
        
        // Process in batches of 20 to avoid blocking the JS thread
        const BATCH_SIZE = 20;
        for (let i = 0; i < files.length; i += BATCH_SIZE) {
          const batch = files.slice(i, i + BATCH_SIZE);
          
          const batchPromises = batch.map(async (filename) => {
            const ext = filename.split('.').pop()?.toLowerCase() || '';
            if (!ALLOWED_EXTENSIONS.has(ext)) {
              return null;
            }

            // Skip 'Sent' folder or hidden/temporary files if they accidentally appear as loose files
            if (filename.startsWith('.')) {
              return null;
            }

            const fileUri = dir.path + filename;
            try {
              const fileInfo = await FileSystem.getInfoAsync(fileUri);
              
              if (fileInfo.exists && !fileInfo.isDirectory) {
                // Expo FileSystem modificationTime is generally in seconds. Convert to ms for Date.now() comparison.
                const modifiedTime = fileInfo.modificationTime ? fileInfo.modificationTime * 1000 : 0;
                
                if (modifiedTime > lastScanTs) {
                  return {
                    uri: fileUri,
                    filename,
                    size: fileInfo.size || 0,
                    modifiedTime,
                    type: dir.type,
                    mimeType: getMimeType(ext)
                  } as WhatsAppFile;
                }
              }
            } catch (e) {
              // Error reading single file, gracefully skip
            }
            return null;
          });

          const results = await Promise.all(batchPromises);
          for (const res of results) {
            if (res) {
              foundFiles.push(res);
            }
          }
          
          // Yield between batches to keep JS event loop responsive
          await new Promise(resolve => setTimeout(resolve, 0));
        }
      }

      // Sort descending by modifiedTime (most recent first)
      foundFiles.sort((a, b) => b.modifiedTime - a.modifiedTime);
      
      // Limit to max 50 files
      setNewFiles(foundFiles.slice(0, 50));
      
    } catch (error) {
      console.error('Error in useWhatsAppWatcher checkFiles:', error);
    } finally {
      setIsChecking(false);
    }
  };

  useEffect(() => {
    // Check files on initial mount
    checkFiles();

    // Setup app state listener to check files when returning to foreground
    const subscription = AppState.addEventListener('change', (nextAppState: AppStateStatus) => {
      if (
        appState.current.match(/inactive|background/) &&
        nextAppState === 'active'
      ) {
        checkFiles();
      }
      appState.current = nextAppState;
    });

    return () => {
      subscription.remove();
    };
  }, []);

  const dismissFiles = async () => {
    try {
      // Update the stored timestamp to now, so we don't pick up these files again
      await AsyncStorage.setItem(LAST_SCAN_KEY, Date.now().toString());
      setNewFiles([]);
    } catch (error) {
      console.error('Error saving last scan timestamp in useWhatsAppWatcher:', error);
    }
  };

  return {
    newFiles,
    newFileCount: newFiles.length,
    dismissFiles,
    isChecking
  };
}
