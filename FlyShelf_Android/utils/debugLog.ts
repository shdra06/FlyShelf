// Debug Log utility for FlyShelf Android — smart in-memory and crash logging
// Deduplicates rapid repeated logs, tracks app lifecycle, and persists crash telemetry

import { Platform, AppState, AppStateStatus } from 'react-native';
import AsyncStorage from '@react-native-async-storage/async-storage';
import NetInfo, { NetInfoState } from '@react-native-community/netinfo';

export const CRASH_LOG_STORAGE_KEY = 'flyshelf_crash_log';

const MAX_LOG_ENTRIES = 200;
const MAX_NET_ENTRIES = 300;
const REPEAT_WINDOW_MS = 3000; // 3-second deduplication window
const REPEAT_MAX_THRESHOLD = 100; // Force flush if repeated 100 times

const logEntries: string[] = [];
const networkEntries: string[] = [];

// Tags that qualify as network-related logs
const NET_TAGS = [
  'FIREBASE', 'PC-POLL', 'IMG-DL', 'DL-QUEUE', 'DOWNLOAD',
  'SYNC', 'AUTH', 'HTTP', 'PAIR', 'CLOUDFLARE', 'CF_',
  'NETWORK', 'SERVER', 'HEARTBEAT', 'SCREENSHOT', 'MEDIA',
  'SYNC_CRYPTO', 'SYNC_CLEANUP', 'SYNC_TRACK', 'CLEANUP',
  'WEBSOCKET', 'WS', 'UPLOAD', 'CONNECT', 'DISCONNECT',
  'LONG-POLL', 'FORCE-SYNC',
];

let _networkLogListeners: Array<() => void> = [];
let _lastListenerNotifyTime = 0;

// Internal state for deduplication
interface RepeatTracker {
  displayTag: string;
  displayMsg: string;
  firstTimestamp: number;
  lastTimestamp: number;
  firstTimeStr: string;
  baseEntry: string;
  count: number;
  isNetwork: boolean;
}

let _activeRepeat: RepeatTracker | null = null;
let _repeatTimer: ReturnType<typeof setTimeout> | null = null;

// Crypto strategy tracking
let _cryptoStrategy = 'uninitialized';

export const setCryptoStrategy = (strategy: string): void => {
  _cryptoStrategy = strategy;
};

export const getCryptoStrategy = (): string => {
  if (_cryptoStrategy && _cryptoStrategy !== 'uninitialized' && _cryptoStrategy !== 'none') {
    return _cryptoStrategy;
  }
  // Check if any log entry already recorded the strategy
  const found = logEntries.find(entry => entry.includes('SYNC_CRYPTO') && entry.includes('resolved via'));
  if (found) {
    const match = found.match(/resolved via\s+([^\n\r]+)/i);
    if (match && match[1]) {
      _cryptoStrategy = match[1].trim();
      return _cryptoStrategy;
    }
  }
  // Inspect global crypto
  if (typeof (globalThis as any).crypto?.subtle !== 'undefined') {
    return 'global-crypto-available';
  }
  return _cryptoStrategy || 'unknown';
};

// Cached connection summary
let _cachedConnectionSummary = 'Unknown';
try {
  NetInfo.addEventListener((state: NetInfoState) => {
    const isOnline = !!(state.isConnected && state.isInternetReachable !== false);
    _cachedConnectionSummary = `${isOnline ? 'Online' : 'Offline'} (${state.type || 'unknown'})`;
  });
} catch {}

export const getConnectionSummary = async (): Promise<string> => {
  try {
    const state = await NetInfo.fetch();
    const isOnline = !!(state.isConnected && state.isInternetReachable !== false);
    const details = state.details as any;
    const isMetered = details && details.isConnectionExpensive ? ', metered' : '';
    const summary = `${isOnline ? 'Online' : 'Offline'} (${state.type || 'unknown'}${isMetered})`;
    _cachedConnectionSummary = summary;
    return summary;
  } catch {
    return _cachedConnectionSummary;
  }
};

const notifyNetworkListeners = (force = false) => {
  const now = Date.now();
  if (force || now - _lastListenerNotifyTime >= 500) {
    _lastListenerNotifyTime = now;
    _networkLogListeners.forEach(fn => {
      try {
        fn();
      } catch (e) {
        if (typeof __DEV__ !== 'undefined' && __DEV__) console.warn('[debugLog] Listener error:', e);
      }
    });
  }
};

const finalizeActiveRepeat = () => {
  if (_repeatTimer) {
    clearTimeout(_repeatTimer);
    _repeatTimer = null;
  }
  if (!_activeRepeat) return;

  if (_activeRepeat.count > 1) {
    const durationSec = Math.max(0, Math.round((_activeRepeat.lastTimestamp - _activeRepeat.firstTimestamp) / 1000));
    const repeatSummary = `${_activeRepeat.baseEntry} (×${_activeRepeat.count} in ${durationSec}s)`;

    if (logEntries.length > 0) {
      logEntries[0] = repeatSummary;
    }
    if (_activeRepeat.isNetwork && networkEntries.length > 0) {
      networkEntries[0] = repeatSummary;
      notifyNetworkListeners(true);
    }
    if (typeof __DEV__ !== 'undefined' && __DEV__) {
      console.log(`[FlyShelf] ${repeatSummary}`);
    }
  }

  _activeRepeat = null;
};

const restartRepeatTimer = () => {
  if (_repeatTimer) {
    clearTimeout(_repeatTimer);
  }
  _repeatTimer = setTimeout(() => {
    finalizeActiveRepeat();
  }, REPEAT_WINDOW_MS + 100);
};

// ══ Core syncLog with Deduplication ══

export const syncLog = (tag: string, message?: string) => {
  const now = Date.now();
  const ts = new Date().toLocaleTimeString('en-GB', { hour12: false });

  // Support both syncLog('TAG', 'msg') and syncLog('full message') forms
  const displayTag = typeof message === 'string' ? tag : '';
  const displayMsg = typeof message === 'string' ? message : tag;

  // Auto-detect crypto strategy if logged
  if ((displayTag === 'SYNC_CRYPTO' || displayTag === 'CRYPTO') && displayMsg.includes('resolved via')) {
    const match = displayMsg.match(/resolved via\s+([^\n\r]+)/i);
    if (match && match[1]) {
      _cryptoStrategy = match[1].trim();
    }
  }

  // Check if network-related
  const upperTag = (displayTag || displayMsg).toUpperCase();
  const isNetwork = NET_TAGS.some(t => upperTag.includes(t));

  // Check if matching the active repeat group within 3 seconds
  if (
    _activeRepeat &&
    _activeRepeat.displayTag === displayTag &&
    _activeRepeat.displayMsg === displayMsg &&
    now - _activeRepeat.lastTimestamp <= REPEAT_WINDOW_MS
  ) {
    _activeRepeat.count++;
    _activeRepeat.lastTimestamp = now;

    const durationSec = Math.max(0, Math.round((now - _activeRepeat.firstTimestamp) / 1000));
    const repeatLine = `${_activeRepeat.baseEntry} (×${_activeRepeat.count} in ${durationSec}s)`;

    // Update newest entry in-place instead of flooding
    if (logEntries.length > 0) {
      logEntries[0] = repeatLine;
    }
    if (_activeRepeat.isNetwork && networkEntries.length > 0) {
      networkEntries[0] = repeatLine;
      notifyNetworkListeners(false);
    }

    // If repeat threshold exceeded, finalize and reset
    if (_activeRepeat.count >= REPEAT_MAX_THRESHOLD) {
      finalizeActiveRepeat();
    } else {
      restartRepeatTimer();
    }
    return;
  }

  // Different message or repeat window expired: finalize previous repeat
  finalizeActiveRepeat();

  // Construct fresh entry
  const entry = displayTag
    ? `[${ts}] [${displayTag}] ${displayMsg}`
    : `[${ts}] ${displayMsg}`;

  _activeRepeat = {
    displayTag,
    displayMsg,
    firstTimestamp: now,
    lastTimestamp: now,
    firstTimeStr: ts,
    baseEntry: entry,
    count: 1,
    isNetwork,
  };

  logEntries.unshift(entry); // newest first
  if (logEntries.length > MAX_LOG_ENTRIES) logEntries.length = MAX_LOG_ENTRIES;

  if (isNetwork) {
    networkEntries.unshift(entry);
    if (networkEntries.length > MAX_NET_ENTRIES) networkEntries.length = MAX_NET_ENTRIES;
    notifyNetworkListeners(true);
  }

  // Also console.log for adb logcat
  if (typeof __DEV__ !== 'undefined' && __DEV__) {
    console.log(`[FlyShelf] ${entry}`);
  }

  restartRepeatTimer();
};

export const getDebugLogs = (): string => {
  return logEntries.join('\n');
};

export const clearDebugLogs = () => {
  finalizeActiveRepeat();
  logEntries.length = 0;
};

// ══ Network-only logs ══

export const getNetworkLogs = (): string[] => {
  return [...networkEntries];
};

export const getNetworkLogsText = (): string => {
  return networkEntries.join('\n');
};

export const clearNetworkLogs = () => {
  if (_activeRepeat?.isNetwork) {
    finalizeActiveRepeat();
  }
  networkEntries.length = 0;
  notifyNetworkListeners(true);
};

export const getNetworkLogCount = (): number => networkEntries.length;

/** Subscribe to network log changes — returns unsubscribe function */
export const onNetworkLogChange = (listener: () => void): (() => void) => {
  _networkLogListeners.push(listener);
  return () => {
    _networkLogListeners = _networkLogListeners.filter(fn => fn !== listener);
  };
};

// ══ App Lifecycle Tracking ══

export const logAppStart = (timestamp?: string): void => {
  const ts = timestamp || new Date().toISOString();
  syncLog('APP', `Cold start at ${ts}`);
};

export const logAppBackground = (): void => {
  syncLog('APP', 'Sent to background');
};

export const logAppForeground = (): void => {
  syncLog('APP', 'Returned to foreground');
};

// Auto-track AppState transitions if AppState is available
if (typeof AppState !== 'undefined' && AppState.addEventListener) {
  let prevAppState: AppStateStatus = AppState.currentState || 'unknown';
  try {
    AppState.addEventListener('change', (nextState: AppStateStatus) => {
      if (prevAppState.match(/inactive|background/) && nextState === 'active') {
        logAppForeground();
      } else if (prevAppState === 'active' && nextState.match(/inactive|background/)) {
        logAppBackground();
      }
      prevAppState = nextState;
    });
  } catch {}
}

// ══ Crash Persistence & Telemetry ══

let _cachedCrashInfo: string | null = null;

const formatCrashRecord = (parsed: any): string => {
  const parts: string[] = [];
  if (parsed.timestamp) parts.push(`Time: ${parsed.timestamp}`);
  if (parsed.platform) parts.push(`Platform: ${parsed.platform}`);
  if (parsed.message || parsed.error) parts.push(`Error: ${parsed.message || parsed.error}`);
  if (parsed.stack) parts.push(`Stack:\n${parsed.stack}`);
  if (Array.isArray(parsed.recentLogs) && parsed.recentLogs.length > 0) {
    parts.push(`\n── Last ${parsed.recentLogs.length} Logs Prior to Crash ──\n${parsed.recentLogs.join('\n')}`);
  }
  return parts.join('\n');
};

export const logFatalCrash = async (error: any, stack?: string | null): Promise<void> => {
  finalizeActiveRepeat();

  const message = error instanceof Error ? error.message : (typeof error === 'string' ? error : JSON.stringify(error) || 'Unknown error');
  const fullStack = stack || (error instanceof Error ? error.stack : '') || '';
  const crashText = fullStack ? `${message}\n${fullStack}` : message;

  // Log to in-memory entries immediately
  syncLog('FATAL', crashText);

  // Persist last 50 log entries + crash info to AsyncStorage
  const crashRecord = {
    timestamp: new Date().toISOString(),
    message,
    stack: fullStack,
    platform: `${Platform.OS} (v${Platform.Version})`,
    recentLogs: logEntries.slice(0, 50),
  };

  const serialized = JSON.stringify(crashRecord);
  _cachedCrashInfo = formatCrashRecord(crashRecord);

  try {
    await AsyncStorage.setItem(CRASH_LOG_STORAGE_KEY, serialized);
  } catch (err) {
    if (typeof __DEV__ !== 'undefined' && __DEV__) {
      console.warn('[debugLog] Failed to persist fatal crash log:', err);
    }
  }
};

export const getLastCrashInfo = async (): Promise<string | null> => {
  try {
    const raw = await AsyncStorage.getItem(CRASH_LOG_STORAGE_KEY);
    if (!raw) {
      // Check legacy key if present
      const legacy = await AsyncStorage.getItem('last_crash_error');
      if (legacy) return legacy;
      return null;
    }
    try {
      const parsed = JSON.parse(raw);
      if (parsed && typeof parsed === 'object') {
        const formatted = formatCrashRecord(parsed);
        _cachedCrashInfo = formatted;
        return formatted;
      }
    } catch {
      _cachedCrashInfo = raw;
      return raw;
    }
    _cachedCrashInfo = raw;
    return raw;
  } catch {
    return _cachedCrashInfo;
  }
};

export const clearCrashLog = async (): Promise<void> => {
  _cachedCrashInfo = null;
  try {
    await AsyncStorage.removeItem(CRASH_LOG_STORAGE_KEY);
    await AsyncStorage.removeItem('last_crash_error');
  } catch {}
};

// Pre-fetch crash log asynchronously on boot
getLastCrashInfo().catch(() => {});

// ══ Formatted Debug Report ══

export const getFormattedReport = async (): Promise<string> => {
  const timestamp = new Date().toISOString();
  const platformStr = `${Platform.OS} (v${Platform.Version})`;

  let crashInfo = 'None recorded';
  try {
    const crash = await getLastCrashInfo();
    if (crash) crashInfo = crash;
  } catch {}

  const cryptoStrat = getCryptoStrategy();
  const connectionState = await getConnectionSummary();

  const allLogs = getDebugLogs() || '(No logs recorded)';
  const netLogs = getNetworkLogsText() || '(No network logs recorded)';

  return `═══════════════════════════════════════
        FlyShelf Debug Report
═══════════════════════════════════════
Timestamp: ${timestamp}
Platform: ${platformStr}
Crypto Strategy: ${cryptoStrat}
Connection State: ${connectionState}

═══ Last Crash Info ═══
${crashInfo}

═══ All Logs (${logEntries.length} entries) ═══
${allLogs}

═══ Network Logs (${networkEntries.length} entries) ═══
${netLogs}
═══════════════════════════════════════`;
};

export const getFormattedReportSync = (): string => {
  const timestamp = new Date().toISOString();
  const platformStr = `${Platform.OS} (v${Platform.Version})`;
  const cryptoStrat = getCryptoStrategy();
  const crashInfo = _cachedCrashInfo || 'None recorded';
  const connectionState = _cachedConnectionSummary;
  const allLogs = getDebugLogs() || '(No logs recorded)';
  const netLogs = getNetworkLogsText() || '(No network logs recorded)';

  return `═══════════════════════════════════════
        FlyShelf Debug Report
═══════════════════════════════════════
Timestamp: ${timestamp}
Platform: ${platformStr}
Crypto Strategy: ${cryptoStrat}
Connection State: ${connectionState}

═══ Last Crash Info ═══
${crashInfo}

═══ All Logs (${logEntries.length} entries) ═══
${allLogs}

═══ Network Logs (${networkEntries.length} entries) ═══
${netLogs}
═══════════════════════════════════════`;
};
