import { initializeApp } from "firebase/app";
import { getDatabase } from "firebase/database";

import { initializeAuth, getAuth, getReactNativePersistence, signInAnonymously, onAuthStateChanged } from "firebase/auth";
import ReactNativeAsyncStorage from '@react-native-async-storage/async-storage';

// ═══ XOR Obfuscation Key (matching PC) ═══
const XOR_KEY = "FlyShelf_2026_Desktop";

// "AIzaSyA52ZXmxx1auJshsv-uuayQRHD22D7zdwk" XOR'd with key
const API_KEY_BYTES = [
  0x07, 0x25, 0x03, 0x32, 0x3B, 0x1C, 0x2D, 0x53, 0x6D, 0x68,
  0x68, 0x5F, 0x4E, 0x27, 0x75, 0x04, 0x06, 0x21, 0x07, 0x07,
  0x03, 0x30, 0x41, 0x0C, 0x26, 0x09, 0x1C, 0x3D, 0x34, 0x17,
  0x76, 0x02, 0x00, 0x72, 0x68, 0x3E, 0x01, 0x04, 0x00
];

// "https://advance-sync-default-rtdb.firebaseio.com" XOR'd with key
const DB_URL_BYTES = [
  0x2E, 0x18, 0x0D, 0x23, 0x1B, 0x5F, 0x43, 0x49, 0x3E, 0x56,
  0x46, 0x53, 0x58, 0x3C, 0x21, 0x48, 0x00, 0x12, 0x1A, 0x0C,
  0x5D, 0x22, 0x09, 0x1F, 0x32, 0x1D, 0x09, 0x18, 0x4B, 0x2D,
  0x46, 0x54, 0x50, 0x18, 0x39, 0x2D, 0x17, 0x16, 0x09, 0x15,
  0x1C, 0x15, 0x2F, 0x03, 0x57, 0x30, 0x07, 0x08
];

/** XOR decoding helper matching the C# FirebaseSecrets.Deobfuscate implementation */
function deobfuscate(bytes: number[], key: string): string {
  let result = "";
  for (let i = 0; i < bytes.length; i++) {
    result += String.fromCharCode(bytes[i] ^ key.charCodeAt(i % key.length));
  }
  return result;
}

const firebaseConfig = {
  apiKey: deobfuscate(API_KEY_BYTES, XOR_KEY),
  authDomain: process.env.EXPO_PUBLIC_FIREBASE_AUTH_DOMAIN || "advance-sync.firebaseapp.com",
  projectId: process.env.EXPO_PUBLIC_FIREBASE_PROJECT_ID || "advance-sync",
  storageBucket: process.env.EXPO_PUBLIC_FIREBASE_STORAGE_BUCKET || "advance-sync.firebasestorage.app",
  messagingSenderId: process.env.EXPO_PUBLIC_FIREBASE_MESSAGING_SENDER_ID || "49241495533",
  appId: process.env.EXPO_PUBLIC_FIREBASE_APP_ID || "1:49241495533:web:a774fec697271c1b81f9e4",
  measurementId: process.env.EXPO_PUBLIC_FIREBASE_MEASUREMENT_ID || "G-FHVL9ESM85",
  databaseURL: deobfuscate(DB_URL_BYTES, XOR_KEY)
};

export const firebaseDatabaseUrl = firebaseConfig.databaseURL || "https://advance-sync-default-rtdb.firebaseio.com";

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
