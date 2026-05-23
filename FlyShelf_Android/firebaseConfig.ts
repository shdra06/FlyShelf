import { initializeApp } from "firebase/app";
import { getDatabase } from "firebase/database";

import { initializeAuth, getAuth, getReactNativePersistence, signInAnonymously, onAuthStateChanged } from "firebase/auth";
import ReactNativeAsyncStorage from '@react-native-async-storage/async-storage';

const firebaseConfig = {
  apiKey: process.env.EXPO_PUBLIC_FIREBASE_API_KEY || "",
  authDomain: process.env.EXPO_PUBLIC_FIREBASE_AUTH_DOMAIN || "",
  projectId: process.env.EXPO_PUBLIC_FIREBASE_PROJECT_ID || "",
  storageBucket: process.env.EXPO_PUBLIC_FIREBASE_STORAGE_BUCKET || "",
  messagingSenderId: process.env.EXPO_PUBLIC_FIREBASE_MESSAGING_SENDER_ID || "",
  appId: process.env.EXPO_PUBLIC_FIREBASE_APP_ID || "",
  measurementId: process.env.EXPO_PUBLIC_FIREBASE_MEASUREMENT_ID || "",
  databaseURL: process.env.EXPO_PUBLIC_FIREBASE_DATABASE_URL || ""
};

export const firebaseDatabaseUrl = firebaseConfig.databaseURL || "https://advance-sync-default-rtdb.firebaseio.com";

if (!firebaseConfig.apiKey) {
  console.warn("[FirebaseConfig] Warning: EXPO_PUBLIC_FIREBASE_API_KEY environment variable is not defined!");
}

const app = initializeApp(firebaseConfig);
export const database = getDatabase(app);


// Use initializeAuth with AsyncStorage persistence for token survival across restarts.
// Falls back to getAuth() during hot-reload when auth is already initialized.
let _auth;
try {
  _auth = initializeAuth(app, {
    persistence: getReactNativePersistence(ReactNativeAsyncStorage)
  });
} catch (e) {
  _auth = getAuth(app);
}
export const auth = _auth;

/**
 * Sign in anonymously to Firebase Auth.
 * This generates a unique UID and ID token that Firebase Security Rules
 * can validate (auth != null). No user account required.
 * 
 * Call this once at app startup — the SDK auto-refreshes the token.
 */
export async function ensureFirebaseAuth(): Promise<void> {
  return new Promise((resolve, reject) => {
    // Check if already signed in
    const unsubscribe = onAuthStateChanged(auth, async (user) => {
      unsubscribe();
      if (user) {
        // Already authenticated
        resolve();
      } else {
        try {
          await signInAnonymously(auth);
          resolve();
        } catch (error) {
          console.error('[FirebaseAuth] Anonymous sign-in failed:', error);
          reject(error);
        }
      }
    });
  });
}

/**
 * Returns the current Firebase ID token for use with raw REST API calls.
 * Returns empty string if not authenticated.
 */
export async function getFirebaseIdToken(): Promise<string> {
  const user = auth.currentUser;
  if (!user) return "";
  try {
    return await user.getIdToken();
  } catch {
    return "";
  }
}

export default app;
