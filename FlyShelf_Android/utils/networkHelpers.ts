// Network utility helpers for FlyShelf Android
// Optimized for large file handling (50MB+) and direct LAN discovery
import { decrypt as aesDecrypt } from './syncCrypto';
// @ts-ignore — export names differ between type definitions and runtime API
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
  const parts = host.split('.');
  if (parts.length !== 4) return host === 'localhost' || host === '127.0.0.1';
  const nums = parts.map(Number);
  if (nums.some(n => isNaN(n) || n < 0 || n > 255)) return false;
  return (
    nums[0] === 10 ||
    (nums[0] === 172 && nums[1] >= 16 && nums[1] <= 31) ||
    (nums[0] === 192 && nums[1] === 168) ||
    (nums[0] === 127 && nums[1] === 0 && nums[2] === 0 && nums[3] === 1)
  );
};

/** Validate device URLs to prevent SSRF and external injections */
export const isValidDeviceUrl = (urlStr: string | null | undefined): boolean => {
  if (!urlStr) return false;
  try {
    // Ensure we have a protocol for URL parsing
    const withProto = urlStr.trim().startsWith('http') ? urlStr.trim() : `http://${urlStr.trim()}`;
    const parsed = new URL(withProto);
    const host = parsed.hostname.toLowerCase();
    
    if (host === 'localhost' || host === '127.0.0.1' || host === '::1') {
      return typeof __DEV__ !== 'undefined' && __DEV__;
    }
    
    if (host.startsWith('169.254.')) return false; // Link-local usually useless for us
    if (host.endsWith('.trycloudflare.com') || host === 'trycloudflare.com') return true;
    
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
    } catch (err) {
      // Decryption failed — return the ORIGINAL device with UrlsEncrypted unchanged
      // so downstream code doesn't try to use half-decrypted / garbled URLs.
      console.warn('[decryptDevice] Decryption failed, preserving original encrypted device:', err);
      return device;
    }
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
  // Merge caller's signal with the timeout signal so external aborts are honoured
  const signal = options?.signal
    ? (typeof AbortSignal.any === 'function'
      ? AbortSignal.any([options.signal, controller.signal])
      : (() => {
          // Fallback: listen for caller abort and forward to our controller
          options.signal!.addEventListener('abort', () => controller.abort(), { once: true });
          return controller.signal;
        })())
    : controller.signal;
  try {
    const response = await fetch(url, { ...options, signal });
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
export const getConnectionType = (device: any, myLocalIp: string): 'LAN' | 'Cloud' | 'Offline' => {
  if (device._isOffline) return 'Offline';
  const deviceIp = device.LocalIp || device.Url || '';
  const mySubnet = getSubnet(myLocalIp);
  const deviceSubnet = getSubnet(deviceIp);
  if (mySubnet && deviceSubnet && mySubnet === deviceSubnet) return 'LAN';
  return 'Cloud';
};

/** Color map for connection types */
export const connectionColors: Record<string, string> = {
  LAN: '#34D399',     // accent.success (returned by getConnectionType)
  Local: '#34D399',   // accent.success (backward compat alias)
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
  // Check if this is a Cloudflare tunnel URL by parsing the hostname properly
  try {
    const withProto = url.startsWith('http') ? url : `https://${url}`;
    const parsed = new URL(withProto);
    if (parsed.hostname.endsWith('.trycloudflare.com')) return withProto.replace(/\/$/, '');
  } catch { /* not a valid URL yet, continue with normalization */ }
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
 * Centrally resolve the best PC URL from the global pairedDevices state.
 * This ensures consistency across all app tabs (Sync, Todo, Notes).
 */
export const resolveBestPcUrl = (pairedDevices: any[], manualIp?: string): string | null => {
  // 1. Look for a PC in the paired list that is currently online
  const pc = pairedDevices.find(d => d.deviceType === 'PC' && d.isOnline);
  if (pc) {
    // Prioritize LAN/Local URL if available and verified
    if (pc.localUrl) return pc.localUrl.replace(/\/$/, '');
    // Fallback to Cloudflare
    if (pc.globalUrl) return pc.globalUrl.replace(/\/$/, '');
  }

  // 2. If no online PC found, look for any PC to get its last known URLs
  const lastPc = pairedDevices.find(d => d.deviceType === 'PC');
  if (lastPc) {
    if (lastPc.localUrl) return lastPc.localUrl.replace(/\/$/, '');
    if (lastPc.globalUrl) return lastPc.globalUrl.replace(/\/$/, '');
  }

  // 3. Last resort: use the manual IP from settings
  if (manualIp) {
    const trimmed = manualIp.trim();
    if (!trimmed) return null;
    if (trimmed.startsWith('http')) return trimmed.replace(/\/$/, '');
    const withPort = trimmed.includes(':') ? trimmed : `${trimmed}:3000`;
    return `http://${withPort}`;
  }

  return null;
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
        // Stagger Cloudflare by 800ms to prefer LAN response even if it's slightly slower
        if (url.includes('trycloudflare.com')) {
          await new Promise(r => setTimeout(r, 800));
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

/** Last discovered PC IP for fast re-probe */
let _lastDiscoveredPcIp: string | null = null;

/**
 * Parallel Subnet Scanner — Discovery without Firebase.
 * Scans the current /24 subnet on common FlyShelf ports.
 * Optimized: caches last discovered IP for fast re-probe.
 */
export const scanSubnetForPc = async (myIp: string): Promise<string[]> => {
  const subnet = getSubnet(myIp);
  if (!subnet) return [];
  
  const ports = [3000, 8080, 8999];
  const PROBE_TIMEOUT = 200; // LAN latency is <5ms, 200ms is generous
  
  // Fast path: probe last known IP first
  if (_lastDiscoveredPcIp && _lastDiscoveredPcIp.startsWith(subnet)) {
    for (const port of ports) {
      try {
        const url = `http://${_lastDiscoveredPcIp}:${port}`;
        const res = await fetchWithTimeout(`${url}/api/health`, { method: 'GET' }, PROBE_TIMEOUT);
        if (res.ok) return [url];
      } catch {}
    }
    _lastDiscoveredPcIp = null; // Stale — clear and do full scan
  }
  
  const CHUNK_SIZE = 50;
  const discovered: string[] = [];
  
  for (let i = 1; i <= 254; i += CHUNK_SIZE) {
    const chunk = Array.from({ length: Math.min(CHUNK_SIZE, 255 - i) }, (_, j) => i + j);
    try {
      // Use Promise.any to return as soon as first PC found in this chunk
      const found = await Promise.any(chunk.flatMap(nodeId => {
        const targetIp = `${subnet}${nodeId}`;
        return ports.map(async port => {
          const url = `http://${targetIp}:${port}`;
          const res = await fetchWithTimeout(`${url}/api/health`, { method: 'GET' }, PROBE_TIMEOUT);
          if (res.ok) {
            _lastDiscoveredPcIp = targetIp; // Cache for next time
            return url;
          }
          throw new Error();
        });
      }));
      discovered.push(found);
      break; // Found one, stop scanning
    } catch {
      // No PC in this chunk, continue to next
    }
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

/**
 * Unified URL resolution with fallback chain.
 * Consolidates duplicate resolution logic from index.tsx.
 */
export const resolveUrlWithFallbacks = async (
  device: any,
  lastWorkingUrl: string | null,
  manualIp: string | undefined,
  getCachedUrl?: () => Promise<string>,
): Promise<string | null> => {
  // 1. Try optimal URL resolution
  if (device && device !== 'Global') {
    const resolved = await resolveOptimalUrl(device);
    if (resolved) return resolved;
  }
  
  // 2. Try cached PC URL resolver
  if (getCachedUrl) {
    try {
      const cached = await getCachedUrl();
      if (cached) return cached;
    } catch {}
  }
  
  // 3. Try last working URL
  if (lastWorkingUrl) return lastWorkingUrl;
  
  // 4. Try manual IP from settings
  if (manualIp?.trim()) {
    const rawParts = manualIp.split(',').map(s => s.trim()).filter(Boolean);
    if (rawParts.length > 0) {
      const raw = rawParts[0];
      return raw.startsWith('http') ? raw.replace(/\/$/, '') : `http://${raw.includes(':') ? raw : raw + ':3000'}`;
    }
  }
  
  return null;
};
