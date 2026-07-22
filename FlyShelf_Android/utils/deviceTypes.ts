/**
 * Shared device type interfaces — replaces 'any' usage across the codebase.
 * Mirrors the PC's C# device models.
 */

/** A device discovered via Firebase active_devices or LAN scanning */
export interface ActiveDeviceInfo {
  DeviceId?: string;
  DeviceName?: string;
  DeviceType?: 'PC' | 'Android' | 'iOS' | string;
  LocalIp?: string;
  Url?: string;
  GlobalUrl?: string;
  TlsUrl?: string;
  IsOnline?: boolean;
  Timestamp?: number;
  UrlsEncrypted?: boolean;
  _isOffline?: boolean;
  [key: string]: any;  // Allow additional Firebase fields
}

/** A paired device stored locally */
export interface PairedDevice {
  deviceId: string;
  deviceName: string;
  deviceType: 'PC' | 'Mobile' | 'Browser' | 'Android' | 'iOS' | string;
  pairedAt?: number;
  isPro?: boolean;
  licenseKey?: string;
  isOnline?: boolean;
  connectionType?: string;
  latencyMs?: number;
  localUrl?: string;
  globalUrl?: string;
  lastSeen?: number;
}

/** Media-bearing clip item (for getMediaUrl) */
export interface MediaClipItem {
  CachedUri?: string;
  Raw?: string;
  DownloadUrl?: string;
  PreviewUrl?: string;
}
