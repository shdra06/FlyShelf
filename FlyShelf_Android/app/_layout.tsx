// MUST be first — installs global.crypto polyfill (Hermes lacks crypto.subtle)
import { install as installCrypto } from 'react-native-quick-crypto';
installCrypto();

import NetInfo from '@react-native-community/netinfo';

import { DarkTheme, DefaultTheme, ThemeProvider } from '@react-navigation/native';
import { Stack } from 'expo-router';
import { StatusBar } from 'expo-status-bar';
import { LogBox, useColorScheme, NativeModules, NativeEventEmitter } from 'react-native';
import 'react-native-reanimated';
import { useEffect, useCallback } from 'react';
import { router } from 'expo-router';
import * as BackgroundFetch from 'expo-background-fetch';
import * as TaskManager from 'expo-task-manager';
import * as SplashScreen from 'expo-splash-screen';
import { GestureHandlerRootView } from 'react-native-gesture-handler';
import { useFonts, Inter_400Regular, Inter_500Medium, Inter_600SemiBold, Inter_700Bold, Inter_800ExtraBold } from '@expo-google-fonts/inter';
import * as Notifications from 'expo-notifications';
import { database } from '../firebaseConfig';
import { ref, get, query, limitToLast } from 'firebase/database';
import { getSecureItem } from '../utils/secureStorage';
import { logAppStart } from '../utils/debugLog';

SplashScreen.preventAutoHideAsync();

// Ignore non-fatal warnings
LogBox.ignoreLogs([
  'Due to changes in Androids permission requirements',
  '@firebase/database: FIREBASE WARNING'
]);

// Defensive guard for ScrollResponder keyboard scroll
import { ScrollView } from 'react-native';
if (ScrollView && (ScrollView as any).prototype) {
  const proto = (ScrollView as any).prototype;
  if (!proto.scrollTo) {
    proto.scrollTo = function(options: any) {
      try {
        if (typeof this.scrollToOffset === 'function') {
          this.scrollToOffset({ offset: options?.y ?? options?.x ?? 0, animated: options?.animated ?? true });
        }
      } catch {}
    };
  }
}


import { SettingsProvider } from '../context/SettingsContext';
import { ToastProvider } from '../context/ToastContext';
import ErrorBoundary from '../components/ErrorBoundary';
import { evictImageCache, evictConvertedPdfs } from '../components/CachedImage';

export const unstable_settings = {
  anchor: '(tabs)',
};

Notifications.setNotificationHandler({
  handleNotification: async () => ({
    shouldShowAlert: true,
    shouldPlaySound: true,
    shouldSetBadge: false,
    shouldShowBanner: true,
    shouldShowList: true,
    }),
});

import { Platform } from 'react-native';

import AsyncStorage from '@react-native-async-storage/async-storage';

const BACKGROUND_FETCH_TASK = 'background-clipboard-sync';

if (Platform.OS !== 'web') {
  TaskManager.defineTask(BACKGROUND_FETCH_TASK, async () => {
    try {
       // C-11: Check network connectivity before attempting Firebase query
       // Prevents Android from counting failures and disabling the background task
       const netState = await NetInfo.fetch();
       if (!netState.isConnected || !netState.isInternetReachable) {
         return BackgroundFetch.BackgroundFetchResult.NoData;
       }

       // M-10 FIX: Wrap secure storage access in try-catch — native modules may
       // not be fully initialized in background context. Fallback to AsyncStorage.
       let pk: string | null = null;
       try {
         pk = await getSecureItem('pairingKey');
       } catch (secureErr) {
         // Fallback: try plain AsyncStorage if SecureStore fails in background
         try {
           pk = await AsyncStorage.getItem('pairingKey');
         } catch {}
       }
       if (!pk) return BackgroundFetch.BackgroundFetchResult.NoData;

       // ─── ENHANCED BACKGROUND SYNC: Actually fetch latest items from PC ───
       // Try to reach PC via last known URL and sync recent items
       let didSync = false;
       try {
         // Try cached Cloudflare URL first, then last known URL
         const cachedUrl = await getSecureItem('lastCloudflareUrl').catch(() => null)
           || await AsyncStorage.getItem('lastWorkingPcUrl').catch(() => null);
         
         if (cachedUrl) {
           const lastSyncStr = await AsyncStorage.getItem('flyshelf_lastSyncTimestamp');
           const lastSync = parseInt(lastSyncStr || '0', 10);
           const syncUrl = lastSync > 0
             ? `${cachedUrl}/api/sync?since=${lastSync}`
             : `${cachedUrl}/api/sync?limit=3`;
           
           const headers: Record<string, string> = {
             'X-FlyShelf-Client': 'MobileCompanion',
             'X-Pairing-Key': pk,
           };
           
           const controller = new AbortController();
           const timeoutId = setTimeout(() => controller.abort(), 8000);
           try {
             const res = await fetch(syncUrl, { headers, signal: controller.signal });
             clearTimeout(timeoutId);
             if (res.ok) {
               const contentType = res.headers.get('content-type') || '';
               if (contentType.includes('application/json')) {
                 const data = await res.json();
                 if (Array.isArray(data) && data.length > 0) {
                   // Update lastSyncTimestamp
                   let maxTs = lastSync;
                   for (const item of data) {
                     if (item.Timestamp && item.Timestamp > maxTs) maxTs = item.Timestamp;
                   }
                   if (maxTs > lastSync) {
                     await AsyncStorage.setItem('flyshelf_lastSyncTimestamp', String(maxTs));
                   }
                   
                   // Push new items to native overlay so floating ball shows fresh data
                   try {
                     const { NativeModules: NM } = require('react-native');
                     if (NM?.AdvanceOverlay?.pushClipToNativeDB) {
                       // Push at most 3 newest items to overlay
                       const newest = data.slice(0, 3);
                       for (const item of newest) {
                         const raw = item.Raw || item.Title || '';
                         if (raw) NM.AdvanceOverlay.pushClipToNativeDB(raw, 'PC');
                       }
                     }
                   } catch {}
                   
                   didSync = true;
                   
                   // Send notification about new items
                   await Notifications.scheduleNotificationAsync({
                     content: {
                       title: `FlyShelf — ${data.length} New Item${data.length > 1 ? 's' : ''}`,
                       body: data[0]?.Title?.substring(0, 60) || 'New clipboard items from PC',
                     },
                     trigger: { type: Notifications.SchedulableTriggerInputTypes.TIME_INTERVAL, seconds: 1 },
                   });
                 }
               }
             }
           } catch (fetchErr) {
             clearTimeout(timeoutId);
           }
         }
       } catch {}

       // ─── Fallback: Firebase wake signal check (original behavior) ───
       if (!didSync) {
         const lastNotifiedStr = await AsyncStorage.getItem('lastNotifiedTimestamp');
         const lastNotified = lastNotifiedStr ? parseInt(lastNotifiedStr) : 0;

         const snaps = await get(ref(database, `active_devices/${pk}/wakeSignal`));
         if (snaps.exists()) {
           const latestTs = typeof snaps.val() === 'number' ? snaps.val() : (snaps.val()?.ts || snaps.val()?.timestamp || 0);

           if (latestTs > lastNotified) {
             await AsyncStorage.setItem('lastNotifiedTimestamp', latestTs.toString());
             await Notifications.scheduleNotificationAsync({
               content: {
                 title: "FlyShelf Mesh Updated",
                 body: "New updates available from your PC. Tap to sync!",
               },
               trigger: { type: Notifications.SchedulableTriggerInputTypes.TIME_INTERVAL, seconds: 1 },
             });
             return BackgroundFetch.BackgroundFetchResult.NewData;
           }
         }
       }

       return didSync ? BackgroundFetch.BackgroundFetchResult.NewData : BackgroundFetch.BackgroundFetchResult.NoData;
    } catch (err) {
       return BackgroundFetch.BackgroundFetchResult.Failed;
    }
  });
}

// Custom light navigation theme — warm creamy base (Apple-like finish)
const FlyShelfLightTheme = {
  ...DefaultTheme,
  colors: {
    ...DefaultTheme.colors,
    background: '#FAF9F6',
    card: '#FAF9F6',
    border: 'rgba(0,0,0,0.05)',
    text: '#1A1A1A',
    primary: '#4D68DF',
  },
};

// Custom dark navigation theme — matches our deep navy base
const FlyShelfDarkTheme = {
  ...DarkTheme,
  colors: {
    ...DarkTheme.colors,
    background: '#0B0D12',
    card: '#0B0D12',
    border: 'rgba(255,255,255,0.06)',
    text: '#F0F2F5',
    primary: '#6384FF',
  },
};

export default function RootLayout() {
  const colorScheme = useColorScheme();

  const [fontsLoaded] = useFonts({
    Inter_400Regular,
    Inter_500Medium,
    Inter_600SemiBold,
    Inter_700Bold,
    Inter_800ExtraBold,
  });

  const onLayoutRootView = useCallback(async () => {
    if (fontsLoaded) {
      await SplashScreen.hideAsync();
    }
  }, [fontsLoaded]);

  useEffect(() => {
    logAppStart();
    if (Platform.OS !== 'web') {
        TaskManager.isTaskRegisteredAsync(BACKGROUND_FETCH_TASK).then(isRegistered => {
          if (!isRegistered) {
            BackgroundFetch.registerTaskAsync(BACKGROUND_FETCH_TASK, {
              minimumInterval: 60, // 1 minute — Android may throttle but we request the minimum
              stopOnTerminate: false,
              startOnBoot: true,
            }).catch(console.warn);
          }
        }).catch(console.warn);
    }
  }, []);

  // Startup: evict stale image cache + converted PDFs (runs once, non-blocking)
  useEffect(() => {
    evictImageCache().catch(() => {});
    evictConvertedPdfs().catch(() => {});
  }, []);

  useEffect(() => {
    if (Platform.OS === 'android') {
      Notifications.setNotificationChannelAsync('default', {
        name: 'FlyShelf',
        importance: Notifications.AndroidImportance.DEFAULT,
        vibrationPattern: [0, 250],
        lightColor: '#6384FF',
      });
    }
    (async () => {
      const { status } = await Notifications.getPermissionsAsync();
      if (status !== 'granted') {
        await Notifications.requestPermissionsAsync();
      }
    })().catch(console.warn);
  }, []);

  // ─── Share Intent Detection ───
  // When the app opens via Android share sheet, auto-navigate to share-receiver
  useEffect(() => {
    if (!fontsLoaded) return;
    const checkShareIntent = async () => {
      try {
        const ShareIntent = NativeModules.ShareIntent;
        if (!ShareIntent || typeof ShareIntent.getSharedFiles !== 'function') return;
        const result = await ShareIntent.getSharedFiles();
        if (result && ((result.files && result.files.length > 0) || result.text)) {
          // Small delay to ensure navigation is ready
          setTimeout(() => {
            router.push('/share-receiver' as any);
          }, 300);
        }
      } catch {}
    };
    checkShareIntent();

    let sub: any = null;
    try {
      if (NativeModules.ShareIntent) {
        const emitter = new NativeEventEmitter(NativeModules.ShareIntent);
        sub = emitter.addListener('onShareIntentReceived', () => {
          checkShareIntent();
        });
      }
    } catch {}

    return () => {
      sub?.remove?.();
    };
  }, [fontsLoaded]);

  if (!fontsLoaded) return null;

  return (
    <ErrorBoundary>
      <GestureHandlerRootView style={{ flex: 1 }} onLayout={onLayoutRootView}>
        <ThemeProvider value={colorScheme === 'light' ? FlyShelfLightTheme : FlyShelfDarkTheme}>
          <SettingsProvider>
            <ToastProvider>
              <Stack>
                <Stack.Screen name="(tabs)" options={{ headerShown: false }} />
                <Stack.Screen name="pdf-tools" options={{ headerShown: false, animation: 'slide_from_bottom' }} />
                <Stack.Screen name="tools" options={{ headerShown: false, animation: 'slide_from_bottom' }} />
                <Stack.Screen name="settings-modal" options={{ headerShown: false, presentation: 'modal', animation: 'slide_from_bottom' }} />
                <Stack.Screen name="share-receiver" options={{ headerShown: false, presentation: 'transparentModal', animation: 'fade' }} />
              </Stack>
              <StatusBar style={colorScheme === 'light' ? 'dark' : 'light'} translucent backgroundColor="transparent" />
            </ToastProvider>
          </SettingsProvider>
        </ThemeProvider>
      </GestureHandlerRootView>
    </ErrorBoundary>
  );
}

