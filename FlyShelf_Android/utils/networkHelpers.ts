// Network utility helpers for FlyShelf Android
// Optimized for large file handling (50MB+) and direct LAN discovery
import { decrypt as aesDecrypt } from './syncCrypto';
import { decode as quickDecode, encode as quickEncode } from 'react-native-quick-base64';

/** Validate if a pairing key is exactly 32-character hex string */
export const isValidPairingKey = (key: string | null | undefined): boolean => {
  if (!key) return false;
  return /^[a-f0-9]{32}$/i.test(key);
};

/** 
 * Robust private IP check covering RFC1918 and typical local ranges.
 * 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16
 */
const isPrivateIp = (host: string): boolean => {
  return (
    /^(192\.168\.\d{1,3}\.\d{1,3})$/.test(host) ||
    /^(10\.\d{1,3}\.\d{1,3}\.\d{1,3})$/.test(host) ||
    /^(172\.(1[6-9]|2\d|3[0-1])\.\d{1,3}\.\d{1,3})$/.test(host) ||
    host === 'localhost' || host === '127.0.0.1'
  );
};

/** Validate device URLs to prevent SSRF and external injections */
export const isValidDeviceUrl = (urlStr: string | null | undefined): boolean => {
  if (!urlStr) return false;
  try {
    let host = urlStr.trim().replace(/^https?:\/\//i, '').split('/')[0].split(':')[0];
    host = host.toLowerCase();
    
    if (host === 'localhost' || host === '127.0.0.1' || host === '::1') {
      return __DEV__;
    }
    
    if (host.startsWith('169.254.')) return false; // Link-local usually useless for us
    if (host.endsWith('trycloudflare.com')) return true;
    
    return isPrivateIp(host);
  } catch {
    return false;
  }
};

/** Decrypt device URLs if they were encrypted by the PC */
export const decryptDevice = async (device: any): Promise<any> => {
  if (!device) return device;
  if (device.DeviceType === 'PC' && device.UrlsEncrypted) {
    const decrypted = { ...device };
    try {
      if (decrypted.LocalIp) {
        const dec = await aesDecrypt(decrypted.LocalIp);
        if (dec) decrypted.LocalIp = dec;
      }
      if (decrypted.GlobalUrl) {
        const dec = await aesDecrypt(decrypted.GlobalUrl);
        if (dec) decrypted.GlobalUrl = dec;
      }
      if (decrypted.Url) {
        const dec = await aesDecrypt(decrypted.Url);
        if (dec) decrypted.Url = dec;
      }
      if (decrypted.TlsUrl) {
        const dec = await aesDecrypt(decrypted.TlsUrl);
        if (dec) decrypted.TlsUrl = dec;
      }
      decrypted.UrlsEncrypted = false;
    } catch {}
    return decrypted;
  }
  return device;
};

/** Decrypt a list/array of devices */
export const decryptDeviceList = async (devices: any[]): Promise<any[]> => {
  if (!devices || !Array.isArray(devices)) return devices || [];
  const decryptedList: any[] = [];
  for (const d of devices) {
    decryptedList.push(await decryptDevice(d));
  }
  return decryptedList;
};

/** Fetch with configurable timeout and abort safety */
export const fetchWithTimeout = async (url: string, options: any = {}, timeoutMs = 2500) => {
  const controller = new AbortController();
  const id = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetch(url, { ...options, signal: controller.signal });
    clearTimeout(id);
    return response;
  } catch (error) {
    clearTimeout(id);
    throw error;
  }
};

/** 
 * Extract subnet prefix from IP. 
 * Improved: Handles both /24 and broader ranges for initial discovery probes.
 */
export const getSubnet = (ip: string): string => {
  if (!ip) return '';
  const clean = ip.replace(/^https?:\/\//, '').split(':')[0];
  const parts = clean.split('.');
  return parts.length >= 3 ? `${parts[0]}.${parts[1]}.${parts[2]}.` : '';
};

/** Determine connection type for a device relative to this phone */
export const getConnectionType = (device: any, myLocalIp: string): 'Local' | 'Cloud' | 'Offline' => {
  if (device._isOffline) return 'Offline';
  const deviceIp = device.LocalIp || device.Url || '';
  const mySubnet = getSubnet(myLocalIp);
  const deviceSubnet = getSubnet(deviceIp);
  if (mySubnet && deviceSubnet && mySubnet === deviceSubnet) return 'Local';
  return 'Cloud';
};

/** Color map for connection types */
export const connectionColors: Record<string, string> = {
  Local: '#34D399',   // accent.success
  Cloud: '#FBBF24',   // accent.warning
  Offline: '#F87171', // accent.error
};

/**
 * Normalize a raw IP/URL into a clean http:// URL with port.
 * Default port set to 3000 to match FlyShelf PC default.
 */
export const normalizeUrl = (raw: string, defaultPort = 3000): string => {
  let url = raw.trim();
  if (!url) return '';
  if (url.includes('trycloudflare.com')) return url.replace(/\/$/, '');
  if (!url.startsWith('http')) url = `http://${url}`;
  
  const hostPart = url.replace(/^https?:\/\//, '');
  if (!hostPart.includes(':')) url = `${url}:${defaultPort}`;
  return url.replace(/\/$/, '');
};

/** 
 * MEMORY OPTIMIZED: Converts Uint8Array to Base64 without intermediate strings.
 * Critical for 50MB PDF support to prevent OOM.
 */
export const uint8ArrayToBase64 = (data: Uint8Array): string => {
  return quickEncode(data);
};

/**
 * MEMORY OPTIMIZED: Converts Base64 to Uint8Array using JSI.
 */
export const base64ToUint8Array = (base64: string): Uint8Array => {
  return quickDecode(base64);
};

/**
 * Get ordered list of URLs to try for a device.
 */
export const getDeviceUrls = (device: any): string[] => {
  if (!device) return [];
  const urls: string[] = [];
  const seen = new Set<string>();

  const add = (raw: string | undefined) => {
    if (!raw || raw === 'offline') return;
    const parts = raw.split(',');
    for (const part of parts) {
      const trimmed = part.trim();
      if (!trimmed) continue;
      const normalized = normalizeUrl(trimmed);
      if (normalized && isValidDeviceUrl(normalized) && !seen.has(normalized)) {
        seen.add(normalized);
        urls.push(normalized);
      }
    }
  };

  add(device.LocalIp);
  add(device.Url);
  if (device.TlsUrl) add(device.TlsUrl);
  add(device.GlobalUrl);

  return urls;
};

/**
 * Resolve the best reachable URL using parallel races.
 * LAN is prioritized by staggering Cloudflare.
 */
export const resolveOptimalUrl = async (
  device: any,
  fetchFn = fetchWithTimeout,
  pairingKey?: string
): Promise<string | null> => {
  if (!device || device === 'Global') return null;

  const urls = getDeviceUrls(device);
  if (urls.length === 0) return null;
  if (urls.length === 1) return urls[0];

  try {
    return await Promise.any(
      urls.map(async (url) => {
        // Stagger Cloudflare by 400ms to prefer LAN response even if it's slightly slower
        if (url.includes('trycloudflare.com')) {
          await new Promise(r => setTimeout(r, 400));
        }
        const headers: Record<string, string> = { 'X-FlyShelf-Client': 'MobileCompanion' };
        if (pairingKey) headers['X-Pairing-Key'] = pairingKey;
        
        const res = await fetchFn(`${url}/api/health`, { method: 'GET', headers }, 1500);
        if (res.ok) return url;
        throw new Error();
      })
    );
  } catch {
    return null;
  }
};

/**
 * Parallel Subnet Scanner — Discovery without Firebase.
 * Scans the current /24 subnet on common FlyShelf ports.
 */
export const scanSubnetForPc = async (myIp: string): Promise<string[]> => {
  const subnet = getSubnet(myIp);
  if (!subnet) return [];
  
  const ports = [3000, 8080, 8999];
  const candidates: string[] = [];
  const baseIp = subnet;
  
  // We scan in parallel chunks to avoid hitting OS limits
  const CHUNK_SIZE = 50;
  const discovered: string[] = [];
  
  for (let i = 1; i <= 254; i += CHUNK_SIZE) {
    const chunk = Array.from({ length: Math.min(CHUNK_SIZE, 255 - i) }, (_, j) => i + j);
    await Promise.allSettled(chunk.flatMap(nodeId => {
      const targetIp = `${baseIp}${nodeId}`;
      return ports.map(async port => {
        try {
          const url = `http://${targetIp}:${port}`;
          const res = await fetchWithTimeout(`${url}/api/health`, { method: 'GET' }, 400);
          if (res.ok) discovered.push(url);
        } catch {}
      });
    }));
    if (discovered.length > 0) break; // Found at least one, stop scanning
  }
  
  return discovered;
};

/** Build absolute media URL from a clip item */
export const getMediaUrl = (item: any, activeDevices: any[], pcLocalIp: string): string => {
  if (item.CachedUri && (item.CachedUri.startsWith('file://') || item.CachedUri.startsWith('/'))) return item.CachedUri;
  if (item.Raw && item.Raw.startsWith('http')) return item.Raw;
  if (item.DownloadUrl && item.DownloadUrl.startsWith('http')) return item.DownloadUrl;
  if (item.PreviewUrl && item.PreviewUrl.startsWith('http')) return item.PreviewUrl;

  const relUrl = item.PreviewUrl || item.DownloadUrl || item.Raw || '';
  if (!relUrl) return '';

  const pcNode = activeDevices.find((d: any) => d.DeviceType === 'PC');
  if (pcNode) {
    const urls = getDeviceUrls(pcNode);
    if (urls.length > 0) {
      return `${urls[0]}${relUrl.startsWith('/') ? relUrl : '/' + relUrl}`;
    }
  }

  if (pcLocalIp) {
    const base = normalizeUrl(pcLocalIp);
    return `${base}${relUrl.startsWith('/') ? relUrl : '/' + relUrl}`;
  }

  return relUrl;
};
