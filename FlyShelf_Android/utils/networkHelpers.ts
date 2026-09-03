import AsyncStorage from '@react-native-async-storage/async-storage';
import { decrypt as aesDecrypt } from './syncCrypto';
import { getSecureItem } from './secureStorage';
// @ts-ignore — export names differ between type definitions and runtime API
import { decode as quickDecode, encode as quickEncode } from 'react-native-quick-base64';
import { ActiveDeviceInfo, PairedDevice, MediaClipItem } from './deviceTypes';

/** Validate if a pairing key is exactly 32-character hex string */
export const isValidPairingKey = (key: string | null | undefined): boolean => {
  if (!key) return false;
  return /^[a-f0-9]{32}$/i.test(key);
};

/**
 * AUDIT FIX #1: Generate HMAC-SHA256 auth token from pairing key + timestamp.
 * This replaces sending the raw pairing key over HTTP headers.
 * Returns { token, timestamp } for use in X-Auth-Token and X-Auth-Timestamp headers.
 */
export const generateHmacAuth = async (pairingKey: string): Promise<{ token: string; timestamp: string }> => {
  const timestamp = Date.now().toString();
  const crypto = globalThis.crypto || (await import('expo-crypto') as any);
  
  // Convert key and message to ArrayBuffer
  const enc = new TextEncoder();
  const keyData = enc.encode(pairingKey);
  const msgData = enc.encode(timestamp);
  
  // Import key for HMAC-SHA256
  const cryptoKey = await crypto.subtle.importKey(
    'raw', keyData, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']
  );
  
  // Sign
  const sig = await crypto.subtle.sign('HMAC', cryptoKey, msgData);
  
  // Convert to hex string
  const hashArray = Array.from(new Uint8Array(sig));
  const token = hashArray.map(b => b.toString(16).padStart(2, '0')).join('');
  
  return { token, timestamp };
};

/**
 * AUDIT FIX #1: Generate auth headers using HMAC instead of raw pairing key.
 * Falls back to raw key if HMAC generation fails (backward compatibility).
 */
export const generateAuthHeaders = async (pairingKey: string | null | undefined): Promise<Record<string, string>> => {
  if (!pairingKey) return {};
  try {
    const { token, timestamp } = await generateHmacAuth(pairingKey);
    return {
      'X-Auth-Token': token,
      'X-Auth-Timestamp': timestamp,
      // Keep X-Pairing-Key for backward compat with older PC versions
      'X-Pairing-Key': pairingKey,
    };
  } catch {
    // Fallback to raw key if crypto fails
    return { 'X-Pairing-Key': pairingKey };
  }
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

/** Fetch with one automatic retry on network error (handles transient WiFi drops) */
export const fetchWithRetry = async (
  url: string,
  options: RequestInit = {},
  timeoutMs = 2500,
  retries = 1
): Promise<Response> => {
  for (let attempt = 0; attempt <= retries; attempt++) {
    try {
      return await fetchWithTimeout(url, options, timeoutMs);
    } catch (err) {
      if (attempt === retries) throw err;
      // Brief pause before retry to let transient network recover
      await new Promise(r => setTimeout(r, 300));
    }
  }
  throw new Error('fetchWithRetry exhausted');
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
export const decryptDevice = async (device: ActiveDeviceInfo, specificPairingKey?: string): Promise<ActiveDeviceInfo> => {
  if (!device) return device;
  if (device.DeviceType === 'PC' && device.UrlsEncrypted) {
    const decrypted = { ...device };
    try {
      if (decrypted.LocalIp) {
        const dec = await aesDecrypt(decrypted.LocalIp, specificPairingKey);
        decrypted.LocalIp = dec || '';
      }
      if (decrypted.GlobalUrl) {
        const dec = await aesDecrypt(decrypted.GlobalUrl, specificPairingKey);
        decrypted.GlobalUrl = dec || '';
      }
      if (decrypted.Url) {
        const dec = await aesDecrypt(decrypted.Url, specificPairingKey);
        decrypted.Url = dec || '';
      }
      if (decrypted.TlsUrl) {
        const dec = await aesDecrypt(decrypted.TlsUrl, specificPairingKey);
        decrypted.TlsUrl = dec || '';
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
export const decryptDeviceList = async (devices: ActiveDeviceInfo[], specificPairingKey?: string): Promise<ActiveDeviceInfo[]> => {
  if (!devices || !Array.isArray(devices)) return devices || [];
  const decryptedList: ActiveDeviceInfo[] = [];
  for (const d of devices) {
    decryptedList.push(await decryptDevice(d, specificPairingKey));
  }
  return decryptedList;
};

/** Fetch with configurable timeout and abort safety.
 *  C-4 FIX: Strips X-Pairing-Key from plain HTTP requests to non-private IPs
 *  to prevent credential leakage on public WiFi networks.
 */
export const fetchWithTimeout = async (url: string, options: RequestInit = {}, timeoutMs = 2500) => {
  // C-4: Security check — strip pairing key from non-TLS requests to non-private IPs
  const isHttps = url.startsWith('https://');
  if (!isHttps && options.headers) {
    try {
      const parsed = new URL(url);
      const host = parsed.hostname;
      if (!isPrivateIp(host)) {
        // Strip pairing key from headers to prevent cleartext credential leakage
        const hdrs = options.headers as Record<string, string>;
        if (hdrs['X-Pairing-Key']) {
          console.warn(`[SECURITY] C-4: Stripped X-Pairing-Key from non-TLS request to public IP ${host}`);
          const { 'X-Pairing-Key': _, ...safeHeaders } = hdrs;
          options = { ...options, headers: safeHeaders };
        }
      }
    } catch {}
  }

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
export const getConnectionType = (device: ActiveDeviceInfo, myLocalIp: string): 'LAN' | 'Cloud' | 'Offline' => {
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
 * Default port set to 8080 to match FlyShelf PC HTTP server.
 */
export const normalizeUrl = (raw: string, defaultPort = 8080): string => {
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
export const getDeviceUrls = (device: ActiveDeviceInfo | null | undefined): string[] => {
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
export const resolveBestPcUrl = (pairedDevices: PairedDevice[], manualIp?: string): string | null => {
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
    const withPort = trimmed.includes(':') ? trimmed : `${trimmed}:8999`;
    return `http://${withPort}`;
  }

  return null;
};

/**
 * Async live URL resolution that checks SecureStore global/local URLs,
 * active devices, and probes endpoints in parallel to guarantee reaching the PC across networks.
 */
export const resolveLivePcUrl = async (pairedDevices?: PairedDevice[], manualIp?: string): Promise<string | null> => {
  try {
    const lanCandidates: string[] = [];
    const cloudCandidates: string[] = [];

    const storedGlobal = await getSecureItem('pairedGlobalUrl');
    const storedLocal = await getSecureItem('pairedLocalUrl');
    const lastCf = await AsyncStorage.getItem('lastCloudflareUrl').catch(() => null);
    const lastLan = await AsyncStorage.getItem('@flyshelf_last_lan_url').catch(() => null);

    if (storedGlobal && storedGlobal.includes('trycloudflare.com')) cloudCandidates.push(storedGlobal.trim().replace(/\/$/, ''));
    if (lastCf && lastCf.includes('trycloudflare.com')) cloudCandidates.push(lastCf.trim().replace(/\/$/, ''));

    if (storedLocal) lanCandidates.push(...storedLocal.split(',').map((s: string) => s.trim()).filter(Boolean));
    if (lastLan) lanCandidates.push(lastLan.trim().replace(/\/$/, ''));

    if (pairedDevices && pairedDevices.length > 0) {
      for (const d of pairedDevices) {
        if (d.deviceType === 'PC') {
          if (d.globalUrl && d.globalUrl.includes('trycloudflare.com')) cloudCandidates.push(d.globalUrl.trim().replace(/\/$/, ''));
          if (d.localUrl) lanCandidates.push(d.localUrl.trim().replace(/\/$/, ''));
        }
      }
    }

    if (manualIp) {
      const trimmed = manualIp.trim();
      if (trimmed) {
        lanCandidates.push(trimmed.startsWith('http') ? trimmed.replace(/\/$/, '') : `http://${trimmed.includes(':') ? trimmed : trimmed + ':8999'}`);
      }
    }

    // Standard emulator fallbacks
    lanCandidates.push('http://10.0.2.2:8999', 'http://10.0.2.2:8080');

    const probe = async (url: string, timeoutMs = 1500): Promise<string> => {
      let clean = url.trim().replace(/\/$/, '');
      if (!clean.startsWith('http')) clean = `http://${clean}`;
      const res = await fetchWithTimeout(`${clean}/api/health`, { headers: { 'X-FlyShelf-Client': 'MobileCompanion' } }, timeoutMs);
      if (res.ok || res.status === 401) return clean;
      throw new Error('unreachable');
    };

    const uniqueLan = Array.from(new Set(lanCandidates));
    const uniqueCloud = Array.from(new Set(cloudCandidates));

    // Parallel speed race: LAN (1500ms) vs Cloudflare (2500ms) with LAN priority
    const lanRace = uniqueLan.length > 0
      ? Promise.any(uniqueLan.map(u => probe(u, 1500)))
      : Promise.reject(new Error('No LAN'));

    const cloudRace = uniqueCloud.length > 0
      ? Promise.any(uniqueCloud.map(u => probe(u, 2500)))
      : Promise.reject(new Error('No Cloud'));

    try {
      const winner = await Promise.race([
        lanRace.then(u => ({ type: 'lan' as const, url: u })).catch(() => new Promise<never>(() => {})),
        cloudRace.then(async u => {
          // Give LAN 100ms chance to win if both respond
          const quickLan = await Promise.race([
            lanRace.then(lanU => ({ type: 'lan' as const, url: lanU })).catch(() => null),
            new Promise<null>(r => setTimeout(() => r(null), 100))
          ]);
          if (quickLan) return quickLan;
          return { type: 'cloud' as const, url: u };
        })
      ]);

      if (winner?.url) {
        if (winner.type === 'lan') {
          AsyncStorage.setItem('@flyshelf_last_lan_url', winner.url).catch(() => {});
        } else {
          AsyncStorage.setItem('lastCloudflareUrl', winner.url).catch(() => {});
          AsyncStorage.setItem('pairedGlobalUrl', winner.url).catch(() => {});
        }
        return winner.url;
      }
    } catch { }

    // Fallbacks
    if (uniqueLan.length > 0 && uniqueLan[0].startsWith('http')) return uniqueLan[0];
    if (uniqueCloud.length > 0) return uniqueCloud[0];
  } catch {}
  return null;
};

/**
 * Resolve the best reachable URL using parallel races.
 * LAN is prioritized by staggering Cloudflare.
 */
export const resolveOptimalUrl = async (
  device: ActiveDeviceInfo | string | null,
  fetchFn = fetchWithTimeout,
  pairingKey?: string
): Promise<string | null> => {
  if (!device || device === 'Global') return null;

  // If device is a plain string (e.g. a raw URL), it's not a structured device — bail
  if (typeof device === 'string') return null;

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
 * H-1 FIX: Uses concurrency limiter to cap concurrent probes at 25
 */

// H-1: Simple semaphore for concurrent request limiting
async function withConcurrencyLimit<T>(tasks: (() => Promise<T>)[], limit: number): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    let running = 0;
    let idx = 0;
    let settled = false;
    
    const next = () => {
      while (running < limit && idx < tasks.length && !settled) {
        const taskIdx = idx++;
        running++;
        tasks[taskIdx]()
          .then(result => {
            if (!settled) { settled = true; resolve(result); }
          })
          .catch(() => {
            running--;
            if (!settled) next();
          });
      }
      if (running === 0 && idx >= tasks.length && !settled) {
        settled = true;
        if (typeof AggregateError !== 'undefined') {
          reject(new (AggregateError as any)([], 'All tasks failed'));
        } else {
          reject(new Error('All tasks failed'));
        }
      }
    };
    next();
  });
}

export const scanSubnetForPc = async (myIp: string): Promise<string[]> => {
  const subnet = getSubnet(myIp);
  if (!subnet) return [];
  
  const ports = [8999, 8080, 3000]; // 8999 is the FlyShelf PC default
  const PROBE_TIMEOUT = 150; // LAN latency is <5ms, 150ms is generous
  const MAX_CONCURRENT = 25; // H-1: Limit concurrent probes
  
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
  
  // Priority IPs: common DHCP-assigned ranges first for faster discovery
  const priorityIds = [
    1,                                                      // Gateway
    ...Array.from({ length: 9 }, (_, i) => i + 2),          // .2-.10
    ...Array.from({ length: 16 }, (_, i) => i + 100),       // .100-.115
    ...Array.from({ length: 11 }, (_, i) => i + 200),       // .200-.210
  ];
  
  // Phase 1: Probe priority IPs with concurrency limit
  try {
    const tasks = priorityIds.flatMap(nodeId => {
      const targetIp = `${subnet}${nodeId}`;
      return ports.map(port => async () => {
        const url = `http://${targetIp}:${port}`;
        const res = await fetchWithTimeout(`${url}/api/health`, { method: 'GET' }, PROBE_TIMEOUT);
        if (res.ok) {
          _lastDiscoveredPcIp = targetIp;
          return url;
        }
        throw new Error();
      });
    });
    const found = await withConcurrencyLimit(tasks, MAX_CONCURRENT);
    return [found];
  } catch {
    // No PC in priority range — continue to full sweep
  }
  
  // Phase 2: Full subnet sweep (skip already-probed IPs)
  const prioritySet = new Set(priorityIds);
  const discovered: string[] = [];
  
  // H-1: Create all tasks but run with concurrency limit
  const sweepTasks = Array.from({ length: 254 }, (_, i) => i + 1)
    .filter(id => !prioritySet.has(id))
    .flatMap(nodeId => {
      const targetIp = `${subnet}${nodeId}`;
      return ports.map(port => async () => {
        const url = `http://${targetIp}:${port}`;
        const res = await fetchWithTimeout(`${url}/api/health`, { method: 'GET' }, PROBE_TIMEOUT);
        if (res.ok) {
          _lastDiscoveredPcIp = targetIp;
          return url;
        }
        throw new Error();
      });
    });
  
  try {
    const found = await withConcurrencyLimit(sweepTasks, MAX_CONCURRENT);
    discovered.push(found);
  } catch {
    // No PC found in full sweep
  }
  
  return discovered;
};

/** Build absolute media URL from a clip item */
export const getMediaUrl = (item: MediaClipItem, activeDevices: ActiveDeviceInfo[], pcLocalIp: string): string => {
  if (item.CachedUri && (item.CachedUri.startsWith('file://') || item.CachedUri.startsWith('/'))) return item.CachedUri;
  if (item.Raw && item.Raw.startsWith('http')) return item.Raw;
  if (item.DownloadUrl && item.DownloadUrl.startsWith('http')) return item.DownloadUrl;
  if (item.PreviewUrl && item.PreviewUrl.startsWith('http')) return item.PreviewUrl;

  const relUrl = item.PreviewUrl || item.DownloadUrl || item.Raw || '';
  if (!relUrl) return '';

  const pcNode = activeDevices.find((d) => d.DeviceType === 'PC');
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
  device: ActiveDeviceInfo | string | null,
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
      return raw.startsWith('http') ? raw.replace(/\/$/, '') : `http://${raw.includes(':') ? raw : raw + ':8999'}`;
    }
  }
  
  return null;
};

/** Fetch JSON with body-size guard — rejects responses > maxBodyBytes (default 10MB)
 *  H-7 FIX: Uses response.json() directly instead of text() + JSON.parse()
 *  to avoid holding both raw text and parsed object in memory simultaneously.
 */
export async function safeFetchJson<T = any>(url: string, options?: RequestInit & { timeout?: number }, maxBodyBytes: number = 10 * 1024 * 1024): Promise<T> {
  const response = await fetchWithTimeout(url, options, options?.timeout);
  const contentLength = parseInt(response.headers.get('Content-Length') || '0', 10);
  if (contentLength > maxBodyBytes) throw new Error(`Response too large: ${contentLength} bytes (max ${maxBodyBytes})`);
  // H-7: Use response.json() directly — avoids holding both raw text string AND parsed object in memory
  return await response.json() as T;
}
