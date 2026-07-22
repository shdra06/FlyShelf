import { useState, useEffect, useRef, useCallback } from 'react';
import NetInfo, { NetInfoState } from '@react-native-community/netinfo';
import { syncLog } from '../utils/debugLog';

export interface NetworkStatus {
  isOnline: boolean;
  isWifi: boolean;
  networkType: string;
  /** True when transitioning from offline → online */
  justReconnected: boolean;
}

/**
 * Hook that provides network connectivity status.
 * Pauses sync when offline, triggers re-sync on reconnect.
 * 
 * Fixes: H11 (no offline detection)
 */
export function useNetworkStatus(): NetworkStatus {
  const [status, setStatus] = useState<NetworkStatus>({
    isOnline: true,
    isWifi: false,
    networkType: 'unknown',
    justReconnected: false,
  });
  const wasOnlineRef = useRef(true);
  const reconnectTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    const unsubscribe = NetInfo.addEventListener((state: NetInfoState) => {
      const isOnline = !!(state.isConnected && state.isInternetReachable !== false);
      const isWifi = state.type === 'wifi';
      const justReconnected = !wasOnlineRef.current && isOnline;

      if (justReconnected) {
        syncLog('NETWORK', `Reconnected via ${state.type}`);
      } else if (wasOnlineRef.current && !isOnline) {
        syncLog('NETWORK', 'Went offline — sync paused');
      }

      wasOnlineRef.current = isOnline;

      setStatus({
        isOnline,
        isWifi,
        networkType: state.type,
        justReconnected,
      });

      // Clear justReconnected after 2 seconds
      if (justReconnected) {
        if (reconnectTimerRef.current) clearTimeout(reconnectTimerRef.current);
        reconnectTimerRef.current = setTimeout(() => {
          setStatus(prev => ({ ...prev, justReconnected: false }));
        }, 2000);
      }
    });

    return () => {
      unsubscribe();
      if (reconnectTimerRef.current) clearTimeout(reconnectTimerRef.current);
    };
  }, []);

  return status;
}
