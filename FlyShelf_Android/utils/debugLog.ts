// Debug Log utility for FlyShelf Android — stores recent sync events in-memory
// User can copy logs from Settings page for troubleshooting

const MAX_LOG_ENTRIES = 200;
const MAX_NET_ENTRIES = 300;
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

export const syncLog = (tag: string, message?: string) => {
  const ts = new Date().toLocaleTimeString('en-GB', { hour12: false });
  // Support both syncLog('TAG', 'msg') and syncLog('full message') forms
  const displayTag = message ? tag : '';
  const displayMsg = message || tag;
  const entry = message
    ? `[${ts}] [${tag}] ${message}`
    : `[${ts}] ${tag}`;
  
  logEntries.unshift(entry); // newest first
  if (logEntries.length > MAX_LOG_ENTRIES) logEntries.length = MAX_LOG_ENTRIES;

  // Check if this is a network-related log
  const upperTag = (displayTag || displayMsg).toUpperCase();
  const isNetwork = NET_TAGS.some(t => upperTag.includes(t));
  if (isNetwork) {
    networkEntries.unshift(entry);
    if (networkEntries.length > MAX_NET_ENTRIES) networkEntries.length = MAX_NET_ENTRIES;
    // Notify listeners (settings screen)
    _networkLogListeners.forEach(fn => { try { fn(); } catch {} });
  }

  // Also console.log for adb logcat
  console.log(`[FlyShelf] ${entry}`);
};

export const getDebugLogs = (): string => {
  return logEntries.join('\n');
};

export const clearDebugLogs = () => {
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
  networkEntries.length = 0;
  _networkLogListeners.forEach(fn => { try { fn(); } catch {} });
};

export const getNetworkLogCount = (): number => networkEntries.length;

/** Subscribe to network log changes — returns unsubscribe function */
export const onNetworkLogChange = (listener: () => void): (() => void) => {
  _networkLogListeners.push(listener);
  return () => {
    _networkLogListeners = _networkLogListeners.filter(fn => fn !== listener);
  };
};
