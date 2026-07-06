import { useState } from 'react';
import { ClipItem } from '../utils/clipTypes';

/** Represents a device as received from Firebase for force-sync (PascalCase properties + .key) */
export interface ForceSyncDevice {
  key: string;
  DeviceName?: string;
  DeviceType?: string;
  IsOnline?: boolean;
  LocalIp?: string;
  [k: string]: unknown;
}

export interface UseModalsReturn {
  // Merge Modal
  isMergeModalVisible: boolean;
  setIsMergeModalVisible: (v: boolean) => void;
  mergeQueue: ClipItem[];
  setMergeQueue: (v: ClipItem[] | ((prev: ClipItem[]) => ClipItem[])) => void;
  // Force Sync Modal
  isForceSyncModalVisible: boolean;
  setIsForceSyncModalVisible: (v: boolean) => void;
  forceSyncDevices: ForceSyncDevice[];
  setForceSyncDevices: (v: ForceSyncDevice[]) => void;
  // Connect Modal
  isConnectModalVisible: boolean;
  setIsConnectModalVisible: (v: boolean) => void;
  // Camera Options
  isCameraOptionsVisible: boolean;
  setIsCameraOptionsVisible: (v: boolean) => void;
  // QR Scanner
  isQRScannerActive: boolean;
  setIsQRScannerActive: (v: boolean) => void;
  // Image Expander
  expandedImage: string | null;
  setExpandedImage: (v: string | null) => void;
  // Target Modal (upload)
  isTargetModalVisible: boolean;
  setIsTargetModalVisible: (v: boolean) => void;
}

/**
 * Manages all modal visibility states and their associated data.
 * Extracted from SyncScreen for decomposition.
 */
export function useModals(): UseModalsReturn {
  const [isMergeModalVisible, setIsMergeModalVisible] = useState(false);
  const [mergeQueue, setMergeQueue] = useState<ClipItem[]>([]);
  const [isForceSyncModalVisible, setIsForceSyncModalVisible] = useState(false);
  const [forceSyncDevices, setForceSyncDevices] = useState<ForceSyncDevice[]>([]);
  const [isConnectModalVisible, setIsConnectModalVisible] = useState(false);
  const [isCameraOptionsVisible, setIsCameraOptionsVisible] = useState(false);
  const [isQRScannerActive, setIsQRScannerActive] = useState(false);
  const [expandedImage, setExpandedImage] = useState<string | null>(null);
  const [isTargetModalVisible, setIsTargetModalVisible] = useState(false);

  return {
    isMergeModalVisible,
    setIsMergeModalVisible,
    mergeQueue,
    setMergeQueue,
    isForceSyncModalVisible,
    setIsForceSyncModalVisible,
    forceSyncDevices,
    setForceSyncDevices,
    isConnectModalVisible,
    setIsConnectModalVisible,
    isCameraOptionsVisible,
    setIsCameraOptionsVisible,
    isQRScannerActive,
    setIsQRScannerActive,
    expandedImage,
    setExpandedImage,
    isTargetModalVisible,
    setIsTargetModalVisible,
  };
}
