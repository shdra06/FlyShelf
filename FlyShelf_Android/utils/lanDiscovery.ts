// ═══════════════════════════════════════════════════════════════
// lanDiscovery — Persistent LAN PC Discovery for FlyShelf Android
// Provides smart subnet scanning with cached IPs, priority DHCP ranges,
// and network change detection for instant reconnect.
// ═══════════════════════════════════════════════════════════════
import AsyncStorage from '@react-native-async-storage/async-storage';
import NetInfo from '@react-native-community/netinfo';
import { fetchWithTimeout } from './networkHelpers';
import { syncLog } from './debugLog';

const LAN_CACHE_KEY = '@flyshelf_lan_cache';
const MAX_CACHED_IPS = 5;
const CACHED_PROBE_TIMEOUT = 300;  // 300ms for known IPs (generous for LAN)
const SCAN_PROBE_TIMEOUT = 200;     // 200ms for subnet scan
const PORTS = [8999, 8080, 3000];   // 8999 is the actual FlyShelf PC default

interface CachedPcEntry {
  ip: string;
  port: number;
  lastSeen: number;
}

export interface DiscoveryResult {
  url: string;
  ip: string;
  port: number;
  latencyMs: number;
}

// ═══ Cache Management ═══

/** Get all cached PC IPs from persistent storage */
export const getCachedPcIps = async (): Promise<CachedPcEntry[]> => {
  try {
    const raw = await AsyncStorage.getItem(LAN_CACHE_KEY);
    if (!raw) return [];
    const entries: CachedPcEntry[] = JSON.parse(raw);
    // Prune entries older than 7 days
    const cutoff = Date.now() - 7 * 24 * 60 * 60 * 1000;
    return entries.filter(e => e.lastSeen > cutoff);
  } catch {
    return [];
  }
};

/** Add a discovered PC IP to the persistent cache */
export const addToPcIpCache = async (ip: string, port: number): Promise<void> => {
  try {
    const existing = await getCachedPcIps();
    // Remove duplicate if exists
    const filtered = existing.filter(e => !(e.ip === ip && e.port === port));
    // Add new entry at front
    const updated = [{ ip, port, lastSeen: Date.now() }, ...filtered].slice(0, MAX_CACHED_IPS);
    await AsyncStorage.setItem(LAN_CACHE_KEY, JSON.stringify(updated));
  } catch {}
};

/** Clear the entire PC IP cache */
export const clearPcIpCache = async (): Promise<void> => {
  try {
    await AsyncStorage.removeItem(LAN_CACHE_KEY);
  } catch {}
};

// ═══ Smart Discovery ═══

/** Probe a single URL and return timing */
const probeUrl = async (url: string, timeout: number): Promise<{ url: string; latencyMs: number } | null> => {
  const start = Date.now();
  try {
    const res = await fetchWithTimeout(`${url}/api/health`, {
      method: 'GET',
      headers: { 'X-FlyShelf-Client': 'MobileCompanion' },
    }, timeout);
    if (res.ok) {
      return { url, latencyMs: Date.now() - start };
    }
  } catch {}
  return null;
};

/**
 * Smart LAN discovery with multi-tier fallback:
 * 1. Probe cached IPs (fastest — sub-100ms on LAN)
 * 2. Probe priority DHCP ranges (.1, .2-.10, .100-.115, .200-.210)
 * 3. Full subnet sweep in chunks of 50
 */
export const discoverPcOnLan = async (myIp?: string): Promise<DiscoveryResult | null> => {
  // ── Phase 1: Probe cached IPs ──
  const cached = await getCachedPcIps();
  if (cached.length > 0) {
    syncLog('LAN-DISCOVER', `Probing ${cached.length} cached IP(s)...`);
    try {
      const result = await Promise.any(
        cached.map(async (entry) => {
          const url = `http://${entry.ip}:${entry.port}`;
          const hit = await probeUrl(url, CACHED_PROBE_TIMEOUT);
          if (hit) return { ...hit, ip: entry.ip, port: entry.port };
          throw new Error('miss');
        })
      );
      syncLog('LAN-DISCOVER', `Cache hit: ${result.ip}:${result.port} (${result.latencyMs}ms)`);
      // Update cache timestamp
      await addToPcIpCache(result.ip, result.port);
      return result;
    } catch {
      syncLog('LAN-DISCOVER', 'All cached IPs missed — proceeding to subnet scan');
    }
  }

  // ── Phase 2 & 3: Subnet scan ──
  if (!myIp) return null;

  const getSubnet = (ip: string): string => {
    const clean = ip.replace(/^https?:\/\//, '').split(':')[0];
    const parts = clean.split('.');
    return parts.length >= 3 ? `${parts[0]}.${parts[1]}.${parts[2]}.` : '';
  };

  const subnet = getSubnet(myIp);
  if (!subnet) return null;

  // Priority IPs: gateway (.1), common DHCP ranges, then full sweep
  const priorityIds = [
    1,                                      // Gateway
    ...Array.from({ length: 9 }, (_, i) => i + 2),    // .2-.10
    ...Array.from({ length: 16 }, (_, i) => i + 100),  // .100-.115
    ...Array.from({ length: 11 }, (_, i) => i + 200),  // .200-.210
  ];

  // Phase 2: Priority IPs first
  syncLog('LAN-DISCOVER', `Scanning priority IPs on subnet ${subnet}...`);
  const hitFromPriority = await scanIpList(subnet, priorityIds);
  if (hitFromPriority) return hitFromPriority;

  // Phase 3: Full sweep (skip already-probed IPs)
  const prioritySet = new Set(priorityIds);
  const CHUNK_SIZE = 50;

  for (let i = 1; i <= 254; i += CHUNK_SIZE) {
    const chunk = Array.from({ length: Math.min(CHUNK_SIZE, 255 - i) }, (_, j) => i + j)
      .filter(id => !prioritySet.has(id));

    if (chunk.length === 0) continue;

    const hitFromChunk = await scanIpList(subnet, chunk);
    if (hitFromChunk) return hitFromChunk;
  }

  syncLog('LAN-DISCOVER', 'No PC found on subnet');
  return null;
};

/** Scan a list of IP IDs on a subnet, return first hit */
const scanIpList = async (subnet: string, ids: number[]): Promise<DiscoveryResult | null> => {
  try {
    const result = await Promise.any(
      ids.flatMap(nodeId => {
        const targetIp = `${subnet}${nodeId}`;
        return PORTS.map(async port => {
          const url = `http://${targetIp}:${port}`;
          const hit = await probeUrl(url, SCAN_PROBE_TIMEOUT);
          if (hit) {
            // Cache the discovery
            addToPcIpCache(targetIp, port).catch(() => {});
            return { url, ip: targetIp, port, latencyMs: hit.latencyMs };
          }
          throw new Error('miss');
        });
      })
    );
    syncLog('LAN-DISCOVER', `Found PC: ${result.ip}:${result.port} (${result.latencyMs}ms)`);
    return result;
  } catch {
    return null;
  }
};

// ═══ Network Change Detection ═══

/**
 * Register a callback that fires on WiFi connect/disconnect.
 * Returns an unsubscribe function.
 */
export const onNetworkChange = (callback: (isConnected: boolean, type: string) => void): (() => void) => {
  const unsubscribe = NetInfo.addEventListener(state => {
    const isConnected = state.isConnected ?? false;
    const type = state.type ?? 'unknown';
    callback(isConnected, type);
  });
  return unsubscribe;
};
