// MUST be first — installs global.crypto polyfill (Hermes lacks crypto.subtle)
import { install as installCrypto } from 'react-native-quick-crypto';
installCrypto();

import NetInfo from '@react-native-community/netinfo';

import { DarkTheme, DefaultTheme, ThemeProvider } from '@react-navigation/native';
import { Stack } from 'expo-router';
import { StatusBar } from 'expo-status-bar';
import { LogBox, useColorScheme } from 'react-native';
import 'react-native-reanimated';
import { useEffect, useCallback } from 'react';
import * as BackgroundFetch from 'expo-background-fetch';
import * as TaskManager from 'expo-task-manager';
import * as SplashScreen from 'expo-splash-screen';
import { GestureHandlerRootView } from 'react-native-gesture-handler';
import { useFonts, Inter_400Regular, Inter_500Medium, Inter_600SemiBold, Inter_700Bold, Inter_800ExtraBold } from '@expo-google-fonts/inter';
import * as Notifications from 'expo-notifications';
import { database } from '../firebaseConfig';
import { ref, get, query, limitToLast } from 'firebase/database';
import { getSecureItem } from '../utils/secureStorage';

SplashScreen.preventAutoHideAsync();

// Ignore non-fatal warnings
LogBox.ignoreLogs([
  'Due to changes in Androids permission requirements',
  '@firebase/database: FIREBASE WARNING'
]);


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

       // Optimization: Only notify if the latest item is newer than the last time we checked
       const lastNotifiedStr = await AsyncStorage.getItem('lastNotifiedTimestamp');
       const lastNotified = lastNotifiedStr ? parseInt(lastNotifiedStr) : 0;

       const snaps = await get(ref(database, `active_devices/${pk}/wakeSignal`));
       if (snaps.exists()) {
           const latestTs = typeof snaps.val() === 'number' ? snaps.val() : (snaps.val()?.timestamp || 0);

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
       return BackgroundFetch.BackgroundFetchResult.NoData;
    } catch (err) {
       return BackgroundFetch.BackgroundFetchResult.Failed;
    }
  });
}

// Custom light navigation theme — matches our warm gray base (NOT pure white)
const FlyShelfLightTheme = {
  ...DefaultTheme,
  colors: {
    ...DefaultTheme.colors,
    background: '#F5F6FA',
    card: '#F5F6FA',
    border: 'rgba(0,0,0,0.07)',
    text: '#1A1D26',
    primary: '#5570E8',
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
     if (Platform.OS !== 'web') {
         TaskManager.isTaskRegisteredAsync(BACKGROUND_FETCH_TASK).then(isRegistered => {
           if (!isRegistered) {
             BackgroundFetch.registerTaskAsync(BACKGROUND_FETCH_TASK, {
               minimumInterval: 15 * 60,
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
              </Stack>
              <StatusBar style={colorScheme === 'light' ? 'dark' : 'light'} translucent backgroundColor="transparent" />
            </ToastProvider>
          </SettingsProvider>
        </ThemeProvider>
      </GestureHandlerRootView>
    </ErrorBoundary>
  );
}

