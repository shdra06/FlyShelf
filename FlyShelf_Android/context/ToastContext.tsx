import React, { createContext, useContext, useState, useRef, useCallback, useEffect } from 'react';
import {
  View,
  Text,
  StyleSheet,
  Animated,
  TouchableOpacity,
  PanResponder,
  Platform,
  useColorScheme,
  Dimensions,
} from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';

export type ToastType =
  | 'success'
  | 'error'
  | 'warning'
  | 'info'
  | 'sync_lan'
  | 'sync_cloud'
  | 'clipboard';

export interface ToastAction {
  label: string;
  onPress: () => void;
}

export interface ToastOptions {
  id?: string;
  type?: ToastType;
  title: string;
  message?: string;
  detail?: string;
  duration?: number;
  action?: ToastAction;
  haptic?: boolean;
}

interface ToastContextValue {
  show: (options: ToastOptions) => void;
  hide: () => void;
  success: (title: string, message?: string, detail?: string) => void;
  error: (title: string, reason?: string, action?: ToastAction) => void;
  warning: (title: string, message?: string) => void;
  info: (title: string, message?: string) => void;
  syncLan: (title: string, deviceName?: string, detail?: string) => void;
  syncCloud: (title: string, deviceName?: string, detail?: string) => void;
  clipboard: (title: string, preview?: string) => void;
}

const ToastContext = createContext<ToastContextValue | null>(null);

let globalShowToast: ((options: ToastOptions) => void) | null = null;
let globalHideToast: (() => void) | null = null;

/** Global helper for non-React contexts (background tasks, sync hooks, etc.) */
export const toast = {
  show: (options: ToastOptions) => globalShowToast?.(options),
  hide: () => globalHideToast?.(),
  success: (title: string, message?: string, detail?: string) =>
    globalShowToast?.({ type: 'success', title, message, detail }),
  error: (title: string, reason?: string, action?: ToastAction) =>
    globalShowToast?.({ type: 'error', title, message: reason, action, duration: 5500 }),
  warning: (title: string, message?: string) =>
    globalShowToast?.({ type: 'warning', title, message, duration: 4000 }),
  info: (title: string, message?: string) =>
    globalShowToast?.({ type: 'info', title, message }),
  syncLan: (title: string, deviceName?: string, detail?: string) =>
    globalShowToast?.({
      type: 'sync_lan',
      title: title || 'Synced via Direct LAN',
      message: deviceName ? `Connected to ${deviceName}` : 'Synced via LAN',
      detail: detail || '⚡ <5ms P2P',
    }),
  syncCloud: (title: string, deviceName?: string, detail?: string) =>
    globalShowToast?.({
      type: 'sync_cloud',
      title: title || 'Synced via Cloud Tunnel',
      message: deviceName ? `Delivered to ${deviceName}` : 'Synced via cloud',
      detail: detail || '☁️ Remote Sync',
    }),
  clipboard: (title: string, preview?: string) =>
    globalShowToast?.({
      type: 'clipboard',
      title: title || 'Copied to Clipboard',
      message: preview ? preview.slice(0, 80) : undefined,
    }),
};

export const useToast = (): ToastContextValue => {
  const ctx = useContext(ToastContext);
  if (!ctx) {
    return {
      show: toast.show,
      hide: toast.hide,
      success: toast.success,
      error: toast.error,
      warning: toast.warning,
      info: toast.info,
      syncLan: toast.syncLan,
      syncCloud: toast.syncCloud,
      clipboard: toast.clipboard,
    };
  }
  return ctx;
};

export const ToastProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const insets = useSafeAreaInsets();
  const colorScheme = useColorScheme();
  const isDark = colorScheme !== 'light';

  const [currentToast, setCurrentToast] = useState<ToastOptions | null>(null);
  const translateY = useRef(new Animated.Value(-120)).current;
  const opacity = useRef(new Animated.Value(0)).current;
  const scale = useRef(new Animated.Value(0.95)).current;
  const dismissTimeoutRef = useRef<any>(null);
  const lastToastHashRef = useRef<string>('');
  const lastToastTimeRef = useRef<number>(0);

  const hideToast = useCallback(() => {
    if (dismissTimeoutRef.current) {
      clearTimeout(dismissTimeoutRef.current);
      dismissTimeoutRef.current = null;
    }
    Animated.parallel([
      Animated.timing(translateY, {
        toValue: -120,
        duration: 220,
        useNativeDriver: true,
      }),
      Animated.timing(opacity, {
        toValue: 0,
        duration: 180,
        useNativeDriver: true,
      }),
      Animated.timing(scale, {
        toValue: 0.95,
        duration: 220,
        useNativeDriver: true,
      }),
    ]).start(() => {
      setCurrentToast(null);
    });
  }, [translateY, opacity, scale]);

  const showToast = useCallback(
    (opts: ToastOptions) => {
      const now = Date.now();
      const hash = `${opts.type || 'info'}_${opts.title}_${opts.message || ''}`;

      // Deduplication: prevent identical back-to-back toasts within 1.5 seconds
      if (lastToastHashRef.current === hash && now - lastToastTimeRef.current < 1500) {
        return;
      }
      lastToastHashRef.current = hash;
      lastToastTimeRef.current = now;

      if (dismissTimeoutRef.current) {
        clearTimeout(dismissTimeoutRef.current);
        dismissTimeoutRef.current = null;
      }

      // Haptic Feedback
      if (opts.haptic !== false) {
        try {
          if (opts.type === 'error') {
            Haptics.notificationAsync(Haptics.NotificationFeedbackType.Error).catch(() => {});
          } else if (opts.type === 'warning') {
            Haptics.notificationAsync(Haptics.NotificationFeedbackType.Warning).catch(() => {});
          } else if (opts.type === 'success' || opts.type === 'sync_lan' || opts.type === 'clipboard') {
            Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light).catch(() => {});
          }
        } catch {}
      }

      setCurrentToast(opts);

      // Animate In
      translateY.setValue(-100);
      opacity.setValue(0);
      scale.setValue(0.92);

      Animated.parallel([
        Animated.spring(translateY, {
          toValue: insets.top + (Platform.OS === 'ios' ? 6 : 14),
          friction: 8,
          tension: 70,
          useNativeDriver: true,
        }),
        Animated.timing(opacity, {
          toValue: 1,
          duration: 180,
          useNativeDriver: true,
        }),
        Animated.spring(scale, {
          toValue: 1,
          friction: 8,
          tension: 70,
          useNativeDriver: true,
        }),
      ]).start();

      // Duration: errors with actions stay longer for readability
      let duration = opts.duration;
      if (!duration) {
        if (opts.type === 'error') duration = 5500;
        else if (opts.type === 'warning') duration = 4200;
        else duration = 2800;
      }

      dismissTimeoutRef.current = setTimeout(() => {
        hideToast();
      }, duration);
    },
    [insets.top, translateY, opacity, scale, hideToast]
  );

  useEffect(() => {
    globalShowToast = showToast;
    globalHideToast = hideToast;
    return () => {
      globalShowToast = null;
      globalHideToast = null;
    };
  }, [showToast, hideToast]);

  // Swipe up to dismiss
  const panResponder = useRef(
    PanResponder.create({
      onMoveShouldSetPanResponder: (_, gestureState) => gestureState.dy < -6,
      onPanResponderMove: (_, gestureState) => {
        if (gestureState.dy < 0) {
          translateY.setValue(insets.top + 10 + gestureState.dy);
        }
      },
      onPanResponderRelease: (_, gestureState) => {
        if (gestureState.dy < -15 || gestureState.vy < -0.5) {
          hideToast();
        } else {
          Animated.spring(translateY, {
            toValue: insets.top + (Platform.OS === 'ios' ? 6 : 14),
            useNativeDriver: true,
          }).start();
        }
      },
    })
  ).current;

  // Icon & theme resolver
  const getIconConfig = (type?: ToastType) => {
    switch (type) {
      case 'success':
        return { name: 'checkmark-circle' as const, color: '#10B981', bg: 'rgba(16, 185, 129, 0.15)' };
      case 'error':
        return { name: 'alert-circle' as const, color: '#EF4444', bg: 'rgba(239, 68, 68, 0.15)' };
      case 'warning':
        return { name: 'warning' as const, color: '#F59E0B', bg: 'rgba(245, 158, 11, 0.15)' };
      case 'sync_lan':
        return { name: 'flash' as const, color: '#06B6D4', bg: 'rgba(6, 182, 212, 0.15)' };
      case 'sync_cloud':
        return { name: 'cloud-done' as const, color: '#818CF8', bg: 'rgba(129, 140, 248, 0.15)' };
      case 'clipboard':
        return { name: 'copy' as const, color: '#6366F1', bg: 'rgba(99, 102, 241, 0.15)' };
      case 'info':
      default:
        return { name: 'information-circle' as const, color: '#38BDF8', bg: 'rgba(56, 189, 248, 0.15)' };
    }
  };

  const iconCfg = getIconConfig(currentToast?.type);

  return (
    <ToastContext.Provider
      value={{
        show: showToast,
        hide: hideToast,
        success: (t, m, d) => showToast({ type: 'success', title: t, message: m, detail: d }),
        error: (t, r, a) => showToast({ type: 'error', title: t, message: r, action: a }),
        warning: (t, m) => showToast({ type: 'warning', title: t, message: m }),
        info: (t, m) => showToast({ type: 'info', title: t, message: m }),
        syncLan: (t, d, dt) => toast.syncLan(t, d, dt),
        syncCloud: (t, d, dt) => toast.syncCloud(t, d, dt),
        clipboard: (t, p) => toast.clipboard(t, p),
      }}
    >
      {children}

      {currentToast && (
        <Animated.View
          {...panResponder.panHandlers}
          style={[
            styles.container,
            {
              transform: [{ translateY }, { scale }],
              opacity,
            },
          ]}
        >
          <View
            style={[
              styles.pill,
              isDark ? styles.pillDark : styles.pillLight,
              currentToast.type === 'error' && styles.pillError,
              currentToast.type === 'warning' && styles.pillWarning,
            ]}
          >
            {/* Status Icon with soft glow */}
            <View style={[styles.iconContainer, { backgroundColor: iconCfg.bg }]}>
              <Ionicons name={iconCfg.name} size={18} color={iconCfg.color} />
            </View>

            {/* Content Area */}
            <View style={styles.textContainer}>
              <View style={styles.titleRow}>
                <Text
                  style={[styles.title, isDark ? styles.titleDark : styles.titleLight]}
                  numberOfLines={1}
                >
                  {currentToast.title}
                </Text>
                {currentToast.detail ? (
                  <Text style={[styles.detailText, isDark ? styles.detailDark : styles.detailLight]}>
                    {currentToast.detail}
                  </Text>
                ) : null}
              </View>

              {/* Explanatory subtitle / why / how to fix */}
              {currentToast.message ? (
                <Text
                  style={[styles.message, isDark ? styles.messageDark : styles.messageLight]}
                  numberOfLines={2}
                >
                  {currentToast.message}
                </Text>
              ) : null}
            </View>

            {/* Optional Action Button */}
            {currentToast.action ? (
              <TouchableOpacity
                activeOpacity={0.7}
                style={[
                  styles.actionButton,
                  isDark ? styles.actionButtonDark : styles.actionButtonLight,
                ]}
                onPress={() => {
                  try {
                    currentToast.action?.onPress();
                  } finally {
                    hideToast();
                  }
                }}
              >
                <Text style={styles.actionText}>{currentToast.action.label}</Text>
              </TouchableOpacity>
            ) : (
              <TouchableOpacity
                onPress={hideToast}
                hitSlop={{ top: 10, bottom: 10, left: 10, right: 10 }}
                style={styles.closeButton}
              >
                <Ionicons
                  name="close"
                  size={14}
                  color={isDark ? 'rgba(255,255,255,0.4)' : 'rgba(0,0,0,0.35)'}
                />
              </TouchableOpacity>
            )}
          </View>
        </Animated.View>
      )}
    </ToastContext.Provider>
  );
};

const { width } = Dimensions.get('window');
const TOAST_MAX_WIDTH = Math.min(width - 24, 460);

const styles = StyleSheet.create({
  container: {
    position: 'absolute',
    top: 0,
    left: 0,
    right: 0,
    alignItems: 'center',
    zIndex: 999999,
    elevation: 999999,
  },
  pill: {
    width: TOAST_MAX_WIDTH,
    flexDirection: 'row',
    alignItems: 'center',
    paddingVertical: 10,
    paddingHorizontal: 13,
    borderRadius: 18,
    borderWidth: 1,
    shadowColor: '#000',
    shadowOffset: { width: 0, height: 6 },
    shadowOpacity: 0.2,
    shadowRadius: 14,
    elevation: 12,
  },
  pillDark: {
    backgroundColor: 'rgba(18, 22, 31, 0.94)',
    borderColor: 'rgba(255, 255, 255, 0.12)',
  },
  pillLight: {
    backgroundColor: 'rgba(255, 255, 255, 0.96)',
    borderColor: 'rgba(0, 0, 0, 0.08)',
  },
  pillError: {
    borderColor: 'rgba(239, 68, 68, 0.35)',
  },
  pillWarning: {
    borderColor: 'rgba(245, 158, 11, 0.35)',
  },
  iconContainer: {
    width: 30,
    height: 30,
    borderRadius: 15,
    alignItems: 'center',
    justifyContent: 'center',
    marginRight: 10,
  },
  textContainer: {
    flex: 1,
    justifyContent: 'center',
    marginRight: 6,
  },
  titleRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  title: {
    fontSize: 13.5,
    fontFamily: 'Inter_600SemiBold',
    letterSpacing: -0.2,
    flexShrink: 1,
  },
  titleDark: {
    color: '#F3F4F6',
  },
  titleLight: {
    color: '#111827',
  },
  detailText: {
    fontSize: 11,
    fontFamily: 'Inter_500Medium',
    marginLeft: 6,
  },
  detailDark: {
    color: 'rgba(255, 255, 255, 0.5)',
  },
  detailLight: {
    color: 'rgba(0, 0, 0, 0.5)',
  },
  message: {
    fontSize: 12,
    fontFamily: 'Inter_400Regular',
    marginTop: 2,
    lineHeight: 16,
  },
  messageDark: {
    color: 'rgba(255, 255, 255, 0.72)',
  },
  messageLight: {
    color: 'rgba(0, 0, 0, 0.65)',
  },
  actionButton: {
    paddingVertical: 5,
    paddingHorizontal: 10,
    borderRadius: 10,
    marginLeft: 6,
  },
  actionButtonDark: {
    backgroundColor: 'rgba(99, 102, 241, 0.25)',
    borderColor: 'rgba(99, 102, 241, 0.4)',
    borderWidth: 1,
  },
  actionButtonLight: {
    backgroundColor: 'rgba(99, 102, 241, 0.12)',
    borderColor: 'rgba(99, 102, 241, 0.25)',
    borderWidth: 1,
  },
  actionText: {
    fontSize: 12,
    fontFamily: 'Inter_600SemiBold',
    color: '#6366F1',
  },
  closeButton: {
    padding: 4,
    marginLeft: 2,
  },
});
