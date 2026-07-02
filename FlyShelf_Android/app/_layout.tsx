// MUST be first — installs global.crypto polyfill (Hermes lacks crypto.subtle)
import { install as installCrypto } from 'react-native-quick-crypto';
installCrypto();

import { DarkTheme, DefaultTheme, ThemeProvider } from '@react-navigation/native';
import { Stack } from 'expo-router';
import { StatusBar } from 'expo-status-bar';
import { LogBox } from 'react-native';
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

import { useColorScheme } from '@/hooks/use-color-scheme';
import { SettingsProvider } from '../context/SettingsContext';
import ErrorBoundary from '../components/ErrorBoundary';

export const unstable_settings = {
  anchor: '(tabs)',
};

Notifications.setNotificationHandler({
  handleNotification: async () => ({
    shouldShowAlert: true,
    shouldPlaySound: true,
    shouldSetBadge: false,
    }),
});

import { Platform } from 'react-native';

import AsyncStorage from '@react-native-async-storage/async-storage';

const BACKGROUND_FETCH_TASK = 'background-clipboard-sync';

if (Platform.OS !== 'web') {
  TaskManager.defineTask(BACKGROUND_FETCH_TASK, async () => {
    try {
       const pk = await getSecureItem('pairingKey');
       if (!pk) return BackgroundFetch.BackgroundFetchResult.NoData;

       // Optimization: Only notify if the latest item is newer than the last time we checked
       const lastNotifiedStr = await AsyncStorage.getItem('lastNotifiedTimestamp');
       const lastNotified = lastNotifiedStr ? parseInt(lastNotifiedStr) : 0;

       const snaps = await get(query(ref(database, `clipboard/${pk}`), limitToLast(1)));
       if (snaps.exists()) {
           const data = snaps.val();
           const latestKey = Object.keys(data)[0];
           const latestItem = data[latestKey];
           const latestTs = latestItem.Timestamp || 0;

           if (latestTs > lastNotified) {
               await AsyncStorage.setItem('lastNotifiedTimestamp', latestTs.toString());
               await Notifications.scheduleNotificationAsync({
                  content: {
                     title: "FlyShelf Mesh Updated",
                     body: `New payload from ${latestItem.SourceDeviceName || 'PC'}. Tap to sync!`,
                  },
                  trigger: null,
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
         BackgroundFetch.registerTaskAsync(BACKGROUND_FETCH_TASK, {
            minimumInterval: 15 * 60,
            stopOnTerminate: false,
            startOnBoot: true,
         }).catch(console.warn);
     }
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
    })();
  }, []);

  if (!fontsLoaded) return null;

  return (
    <ErrorBoundary>
      <GestureHandlerRootView style={{ flex: 1 }} onLayout={onLayoutRootView}>
        <ThemeProvider value={colorScheme === 'dark' ? DarkTheme : DefaultTheme}>
          <SettingsProvider>
            <Stack>
              <Stack.Screen name="(tabs)" options={{ headerShown: false }} />
              <Stack.Screen name="pdf-tools" options={{ headerShown: false, animation: 'slide_from_bottom' }} />
            </Stack>
            <StatusBar style="auto" />
          </SettingsProvider>
        </ThemeProvider>
      </GestureHandlerRootView>
    </ErrorBoundary>
  );
}

