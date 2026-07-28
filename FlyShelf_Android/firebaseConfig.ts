import { initializeApp } from "firebase/app";
import { getDatabase } from "firebase/database";

import { initializeAuth, getAuth, signInAnonymously, onAuthStateChanged } from "firebase/auth";
// @ts-ignore — getReactNativePersistence location varies by Firebase version
import { getReactNativePersistence } from "firebase/auth";
import ReactNativeAsyncStorage from '@react-native-async-storage/async-storage';

// ═══ XOR Obfuscation Key (matching PC) ═══
const XOR_KEY = "FlyShelf_2026_Desktop";

// API key XOR'd with key (deobfuscated at runtime)
const API_KEY_BYTES = [
  0x07, 0x25, 0x03, 0x32, 0x3B, 0x1C, 0x2D, 0x53, 0x6D, 0x68,
  0x68, 0x5F, 0x4E, 0x27, 0x75, 0x04, 0x06, 0x21, 0x07, 0x07,
  0x03, 0x30, 0x41, 0x0C, 0x26, 0x09, 0x1C, 0x3D, 0x34, 0x17,
  0x76, 0x02, 0x00, 0x72, 0x68, 0x3E, 0x01, 0x04, 0x00
];

// Database URL (XOR-obfuscated)
const DB_URL_BYTES = [
  0x2E, 0x18, 0x0D, 0x23, 0x1B, 0x5F, 0x43, 0x49, 0x3E, 0x56,
  0x46, 0x53, 0x58, 0x3C, 0x21, 0x48, 0x00, 0x12, 0x1A, 0x0C,
  0x5D, 0x22, 0x09, 0x1F, 0x32, 0x1D, 0x09, 0x18, 0x4B, 0x2D,
  0x46, 0x54, 0x50, 0x18, 0x39, 0x2D, 0x17, 0x16, 0x09, 0x15,
  0x1C, 0x15, 0x2F, 0x03, 0x57, 0x30, 0x07, 0x08
];

// Auth domain (XOR-obfuscated)
const AUTH_DOMAIN_BYTES = [
  0x27, 0x08, 0x0f, 0x32, 0x06, 0x06, 0x09, 0x4b, 0x2c, 0x4b,
  0x5e, 0x51, 0x1b, 0x3b, 0x21, 0x03, 0x12, 0x1e, 0x18, 0x1b,
  0x5e, 0x20, 0x05, 0x0b, 0x36, 0x0a, 0x04, 0x1f, 0x03, 0x3e,
  0x42, 0x40, 0x1c, 0x55, 0x30, 0x29
];

// Project ID (XOR-obfuscated)
const PROJECT_ID_BYTES = [
  0x27, 0x08, 0x0f, 0x32, 0x06, 0x06, 0x09, 0x4b, 0x2c, 0x4b,
  0x5e, 0x51, 0x1b, 0x3b, 0x21, 0x03, 0x12, 0x1e, 0x18, 0x1b
];

// Storage bucket (XOR-obfuscated)
const STORAGE_BUCKET_BYTES = [
  0x27, 0x08, 0x0f, 0x32, 0x06, 0x06, 0x09, 0x4b, 0x2c, 0x4b,
  0x5e, 0x51, 0x18, 0x39, 0x2d, 0x17, 0x16, 0x09, 0x15, 0x1c,
  0x15, 0x35, 0x18, 0x16, 0x21, 0x09, 0x02, 0x09, 0x48, 0x3e,
  0x42, 0x40
];

// Messaging sender ID (XOR-obfuscated)
const MESSAGING_SENDER_ID_BYTES = [
  0x72, 0x55, 0x4b, 0x67, 0x59, 0x51, 0x55, 0x53, 0x6a, 0x01,
  0x03
];

// App ID (XOR-obfuscated)
const APP_ID_BYTES = [
  0x77, 0x56, 0x4d, 0x6a, 0x5a, 0x51, 0x5d, 0x52, 0x66, 0x07,
  0x05, 0x01, 0x05, 0x65, 0x33, 0x00, 0x11, 0x51, 0x15, 0x58,
  0x47, 0x72, 0x0a, 0x1c, 0x30, 0x5e, 0x5c, 0x5b, 0x54, 0x68,
  0x03, 0x53, 0x03, 0x54, 0x67, 0x75, 0x03, 0x4a, 0x0e, 0x40
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
  authDomain: deobfuscate(AUTH_DOMAIN_BYTES, XOR_KEY),
  projectId: deobfuscate(PROJECT_ID_BYTES, XOR_KEY),
  storageBucket: deobfuscate(STORAGE_BUCKET_BYTES, XOR_KEY),
  messagingSenderId: deobfuscate(MESSAGING_SENDER_ID_BYTES, XOR_KEY),
  appId: deobfuscate(APP_ID_BYTES, XOR_KEY),
  databaseURL: deobfuscate(DB_URL_BYTES, XOR_KEY)
};

export const firebaseDatabaseUrl = firebaseConfig.databaseURL;


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
let _restIdToken: string | null = null;

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
        // Need to sign in
        try {
          await signInAnonymously(auth);
          resolve();
        } catch (error: any) {
          console.error('[FirebaseAuth] SDK Anonymous sign-in failed:', error);
          
          // ─── FALLBACK: Try REST API if SDK fails (Robustness for Android) ───
          if (error.code === 'auth/network-request-failed' || error.message?.toLowerCase().includes('network')) {
            console.log('[FirebaseAuth] Attempting REST API fallback...');
            try {
              await signInAnonymouslyRest();
              resolve();
            } catch (restError) {
              console.error('[FirebaseAuth] REST fallback also failed:', restError);
              reject(restError);
            }
          } else {
            reject(error);
          }
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
  if (!user) {
    // Try to re-authenticate if no current user
    try {
      await ensureFirebaseAuth();
      // After ensureFirebaseAuth, auth.currentUser might be set if SDK succeeded
      if (auth.currentUser) {
        return await auth.currentUser.getIdToken(true);
      }
      // If still null, check if we have a REST token
      if (_restIdToken) {
        return _restIdToken;
      }
    } catch (error) {}
    return "";
  }
  try {
    return await user.getIdToken();
  } catch (e) {
    console.warn('[FirebaseAuth] Token refresh failed:', e);
    // One retry: force refresh
    try {
      return await user.getIdToken(true);
    } catch {
      return "";
    }
  }
}

export default app;
/**
 * Fallback: Sign in anonymously via Firebase REST API.
 * Bypasses JS SDK internal request handling which can fail on some Android environments.
 */
async function signInAnonymouslyRest(): Promise<void> {
  const apiKey = firebaseConfig.apiKey;
  const url = `https://identitytoolkit.googleapis.com/v1/accounts:signUp?key=${apiKey}`;
  
  try {
    const res = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ returnSecureToken: true })
    });
    
    const data = await res.json();
    if (!res.ok) {
      throw new Error(data.error?.message || 'REST Auth Failed');
    }
    
    if (data.idToken) {
      _restIdToken = data.idToken;
      console.log('[FirebaseAuth] REST sign-in successful, token cached');
      
      // AC-11: Auto-refresh token after 50 minutes instead of just clearing it
      // Firebase tokens last 1 hour — proactively re-authenticate before expiry
      setTimeout(() => {
        console.log('[FirebaseAuth] REST token expiring — refreshing...');
        signInAnonymouslyRest().catch(e => {
          console.warn('[FirebaseAuth] REST token refresh failed:', e);
          _restIdToken = null;
        });
      }, 50 * 60 * 1000);
    }
  } catch (error) {
    throw error;
  }
}
