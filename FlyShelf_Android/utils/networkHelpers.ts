// Network utility helpers for FlyShelf Android
// Simplified: Trust Firebase data, try-then-fallback pattern, no redundant health checks
import { decrypt as aesDecrypt } from './syncCrypto';

/** Validate if a pairing key is exactly 32-character hex string */
export const isValidPairingKey = (key: string | null | undefined): boolean => {
  if (!key) return false;
  return /^[a-f0-9]{32}$/i.test(key);
};

/** Validate device URLs to prevent SSRF and external injections */
export const isValidDeviceUrl = (urlStr: string | null | undefined): boolean => {
  if (!urlStr) return false;
  try {
    // Basic parse to get host
    let host = urlStr.trim().replace(/^https?:\/\//i, '').split('/')[0].split(':')[0];
    host = host.toLowerCase();
    
    if (host === 'localhost' || host === '127.0.0.1' || host === '::1') {
      return __DEV__;
    }
    
    if (host.startsWith('169.254.')) {
      return false;
    }
    
    if (host.endsWith('trycloudflare.com')) {
      return true;
    }
    
    // Allow RFC1918 private subnets
    const isPrivateIp = 
      /^(192\.168\.\d{1,3}\.\d{1,3})$/.test(host) ||
      /^(10\.\d{1,3}\.\d{1,3}\.\d{1,3})$/.test(host) ||
      /^(172\.(1[6-9]|2\d|3[0-1])\.\d{1,3}\.\d{1,3})$/.test(host);
      
    if (isPrivateIp) {
      return true;
    }
    
    return false;
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
      decrypted.UrlsEncrypted = false; // Decrypted successfully
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

/** Fetch with configurable timeout */
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

/** Extract subnet prefix from IP (first 3 octets) */
export const getSubnet = (ip: string): string => {
  if (!ip) return '';
  const clean = ip.replace(/^https?:\/\//, '').split(':')[0];
  const parts = clean.split('.');
  return parts.length >= 3 ? `${parts[0]}.${parts[1]}.${parts[2]}` : '';
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

/** Color map for connection types — uses theme accent colors */
export const connectionColors: Record<string, string> = {
  Local: '#34D399',   // colors.accent.success
  Cloud: '#FBBF24',   // colors.accent.warning
  Offline: '#F87171', // colors.accent.error
};

/**
 * Normalize a raw IP/URL into a clean http:// URL with port.
 * "192.168.1.5"       → "http://192.168.1.5:8999"
 * "192.168.1.5:8999"  → "http://192.168.1.5:8999"
 * "http://192.168.1.5" → "http://192.168.1.5:8999"
 * "https://x.trycloudflare.com/" → "https://x.trycloudflare.com"
 */
const normalizeUrl = (raw: string): string => {
  let url = raw.trim();
  if (!url) return '';
  // Cloudflare URLs are already complete
  if (url.includes('trycloudflare.com')) return url.replace(/\/$/, '');
  // Add http:// if missing
  if (!url.startsWith('http')) url = `http://${url}`;
  // Add port if missing
  const hostPart = url.replace(/^https?:\/\//, '');
  if (!hostPart.includes(':')) url = `${url}:8999`;
  return url.replace(/\/$/, '');
};

/**
 * Get ordered list of URLs to try for a device.
 * Priority: LAN (fastest) → Cloudflare (reliable) → raw Url field
 * No health checks here — caller will try-then-fallback.
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

  // LAN first (lowest latency)
  add(device.LocalIp);
  // Then the Url field (often same as LocalIp, but may differ)
  add(device.Url);
  // TLS LAN (encrypted local — preferred over Cloudflare when on same network)
  if (device.TlsUrl) add(device.TlsUrl);
  // Then Cloudflare (works from anywhere)
  add(device.GlobalUrl);

  return urls;
};

/**
 * Resolve the best reachable URL for a device.
 * Smart approach: trust Firebase data, only health-check if multiple candidates.
 * If only one URL available, use it directly (no wasted round-trip).
 */
export const resolveOptimalUrl = async (
  device: any,
  fetchFn = fetchWithTimeout,
  pairingKey?: string
): Promise<string | null> => {
  if (!device || device === 'Global') return null;

  const urls = getDeviceUrls(device);
  if (urls.length === 0) return null;

  // If only one URL, trust it — don't waste time health-checking
  if (urls.length === 1) return urls[0];

  // Multiple URLs: quick health check to pick the fastest (LAN preferred)
  // Use Promise.race — first healthy response wins
  try {
    const result = await Promise.any(
      urls.map(async (url, idx) => {
        // Stagger Cloudflare by 500ms to prefer LAN
        if (url.includes('trycloudflare.com')) {
          await new Promise(r => setTimeout(r, 500));
        }
        const headers: Record<string, string> = { 'X-FlyShelf-Client': 'MobileCompanion' };
        if (pairingKey) headers['X-Pairing-Key'] = pairingKey;
        const res = await fetchFn(`${url}/api/health`, {
          method: 'GET',
          headers,
        }, 2000);
        if (res.ok) return url;
        throw new Error(`${url} returned ${res.status}`);
      })
    );
    return result;
  } catch {
    // All failed — return null (prevents SSRF redirects)
    return null;
  }
};

/** Build absolute media URL from a clip item */
export const getMediaUrl = (item: any, activeDevices: any[], pcLocalIp: string): string => {
  // PRIORITY 1: Local cached file (already downloaded — most reliable)
  if (item.CachedUri && (item.CachedUri.startsWith('file://') || item.CachedUri.startsWith('/'))) return item.CachedUri;
  // PRIORITY 2: Absolute URL (Cloudflare or Firebase)
  if (item.Raw && item.Raw.startsWith('http')) return item.Raw;
  if (item.DownloadUrl && item.DownloadUrl.startsWith('http')) return item.DownloadUrl;
  if (item.PreviewUrl && item.PreviewUrl.startsWith('http')) return item.PreviewUrl;

  // Relative URL — needs a base
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
