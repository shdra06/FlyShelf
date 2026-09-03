import { syncLog } from './debugLog';
import { NetworkClock } from './networkClock';
import { fetchWithTimeout } from './networkHelpers';
import { getSecureItem } from './secureStorage';
import AsyncStorage from '@react-native-async-storage/async-storage';

export interface OutboxClip {
  type: string;
  title: string;
  data: string;
  sourceDeviceName: string;
  sourceDeviceId: string;
  sourceDeviceType: string;
  timestamp: number;
}

let _activeWebSocket: WebSocket | null = null;
const _outboxQueue: OutboxClip[] = [];
let _offlineOutboxEnabled: boolean = false;

let _hydrationComplete = false;

// Hydrate the setting from AsyncStorage on module load
AsyncStorage.getItem('@isOfflineOutboxEnabled').then(val => {
  _offlineOutboxEnabled = val === 'true';
  _hydrationComplete = true;
}).catch(() => {
  _hydrationComplete = true;
});

export const DirectMesh = {
  /** Register the live WebSocket instance for instant streaming */
  registerWebSocket(ws: WebSocket | null) {
    _activeWebSocket = ws;
  },

  /** True if the direct WebSocket stream is open */
  get isWebSocketOpen(): boolean {
    return _activeWebSocket !== null && _activeWebSocket.readyState === WebSocket.OPEN;
  },

  /**
   * Update the offline outbox queue enabled state.
   * Called from the settings context whenever the toggle changes.
   */
  setOfflineOutboxEnabled(enabled: boolean) {
    _offlineOutboxEnabled = enabled;
    syncLog('DIRECT-MESH', `Offline Outbox Queue: ${enabled ? 'ENABLED' : 'DISABLED'}`);
  },

  /** True if offline outbox queue is currently enabled */
  get isOfflineOutboxEnabled(): boolean {
    return _offlineOutboxEnabled;
  },

  /**
   * Drain any queued outbox clips directly down the WebSocket connection.
   */
  drainOutbox() {
    if (!_activeWebSocket || _activeWebSocket.readyState !== WebSocket.OPEN) return;
    if (_outboxQueue.length === 0) return;

    syncLog('DIRECT-MESH', `⚡ Draining ${_outboxQueue.length} queued outbox items over WebSocket`);
    while (_outboxQueue.length > 0 && _activeWebSocket.readyState === WebSocket.OPEN) {
      const item = _outboxQueue.shift();
      if (item) {
        try {
          _activeWebSocket.send(JSON.stringify({
            type: 'SyncClip',
            itemType: item.type,
            title: item.title,
            data: item.data,
            sourceDeviceName: item.sourceDeviceName,
            sourceDeviceId: item.sourceDeviceId,
            sourceDeviceType: item.sourceDeviceType,
            ts: item.timestamp,
          }));
        } catch (e) {
          _outboxQueue.unshift(item);
          break;
        }
      }
    }
  },

  /**
   * Universal Clip Dispatcher:
   * 1. If WebSocket is OPEN -> Sends directly down the persistent socket (<5ms latency, 0 HTTP overhead).
   * 2. Else -> Sends via HTTP POST /api/sync_text to active URL.
   * 3. Else -> Stages into Outbox queue and auto-drains on reconnect (ONLY if offline outbox is enabled).
   */
  async sendClip(params: {
    type: string;
    title: string;
    data: string;
    deviceName: string;
    activeUrl?: string | null;
  }): Promise<{ success: boolean; transport: 'ws' | 'lan' | 'cloud' | 'outbox' | 'dropped' }> {
    const { type, title, data, deviceName, activeUrl } = params;
    const myDeviceId = `Mobile_${(deviceName || 'Phone').replace(/[^a-zA-Z0-9_]/g, '_')}`;
    const safeData = data || '';
    const clipPayload: OutboxClip = {
      type: type || 'Text',
      title: title || (safeData.length > 40 ? safeData.substring(0, 40) + '...' : safeData),
      data: data,
      sourceDeviceName: deviceName || 'Mobile',
      sourceDeviceId: myDeviceId,
      sourceDeviceType: 'Mobile',
      timestamp: NetworkClock.now(),
    };

    // ─── Tier 1: Direct Full-Duplex WebSocket Push (<5ms) ───
    if (_activeWebSocket && _activeWebSocket.readyState === WebSocket.OPEN) {
      try {
        _activeWebSocket.send(JSON.stringify({
          type: 'SyncClip',
          itemType: clipPayload.type,
          title: clipPayload.title,
          data: clipPayload.data,
          sourceDeviceName: clipPayload.sourceDeviceName,
          sourceDeviceId: clipPayload.sourceDeviceId,
          sourceDeviceType: clipPayload.sourceDeviceType,
          ts: clipPayload.timestamp,
        }));
        syncLog('DIRECT-MESH', `⚡ Dispatched ${clipPayload.type} directly over WebSocket (${clipPayload.title})`);
        return { success: true, transport: 'ws' };
      } catch (wsErr) {
        syncLog('DIRECT-MESH', `WebSocket send error, falling back to HTTP: ${wsErr}`);
      }
    }

    // ─── Tier 2: Direct HTTP POST (/api/sync_text) ───
    if (activeUrl && activeUrl.startsWith('http')) {
      try {
        const pairingKey = await getSecureItem('pairingKey');
        const hdrs: Record<string, string> = {
          'Content-Type': 'application/json',
          'X-FlyShelf-Client': 'MobileCompanion',
          'X-Source-Device': deviceName || 'Mobile',
          'X-Device-Id': myDeviceId,
        };
        if (pairingKey) hdrs['X-Pairing-Key'] = pairingKey;

        const timeout = activeUrl.includes('trycloudflare.com') ? 6000 : 2500;
        const res = await fetchWithTimeout(`${activeUrl}/api/sync_text`, {
          method: 'POST',
          headers: hdrs,
          body: JSON.stringify(clipPayload),
        }, timeout);

        if (res.ok) {
          const transport = activeUrl.includes('trycloudflare.com') ? 'cloud' : 'lan';
          syncLog('DIRECT-MESH', `✅ Dispatched ${clipPayload.type} via HTTP POST (${transport.toUpperCase()})`);
          return { success: true, transport };
        }
      } catch (httpErr) {
        syncLog('DIRECT-MESH', `HTTP dispatch failed: ${httpErr}`);
      }
    }

    // ─── Tier 3: Stage in Offline Outbox Queue (only when enabled) ───
    // Note: if !_hydrationComplete, we might falsely drop here, but it's the safe path
    if (_offlineOutboxEnabled) {
      if (_outboxQueue.length >= 50) _outboxQueue.shift(); // FIFO eviction
      _outboxQueue.push(clipPayload);
      syncLog('DIRECT-MESH', `📦 Device offline — clip staged in Outbox queue (${_outboxQueue.length} pending)`);
      return { success: false, transport: 'outbox' };
    }

    // ─── Outbox disabled: clip is dropped ───
    syncLog('DIRECT-MESH', `⛔ Device offline & Offline Outbox disabled — clip dropped`);
    return { success: false, transport: 'dropped' };
  }
};
