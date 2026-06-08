// ═══════════════════════════════════════════════════════════════════
// Firebase Admin Helper — Authenticated RTDB REST API (v2.2.1)
//
// [SECURITY FIX C2]: All Vercel API endpoints were previously accessing
// the payment Firebase RTDB without any authentication, meaning anyone
// who discovered the RTDB URL could read all customer data.
//
// This module adds authentication to ALL Firebase REST calls using
// the FIREBASE_DB_SECRET env var (legacy database secret from
// Firebase Console → Project Settings → Service Accounts → Database Secrets).
//
// Usage:
//   const { firebaseFetch } = require('./_firebaseAdmin');
//   const data = await firebaseFetch('/payments/xyz.json');
//   await firebaseFetch('/payments/xyz.json', { method: 'PUT', body: ... });
// ═══════════════════════════════════════════════════════════════════

const DB_SECRET = process.env.FIREBASE_DB_SECRET;

/**
 * Authenticated fetch wrapper for Firebase RTDB REST API.
 * Appends ?auth=<secret> (or &auth=<secret>) to authenticate the request.
 * Falls back to unauthenticated if FIREBASE_DB_SECRET is not set (for dev).
 * 
 * @param {string} url - Full Firebase REST URL (e.g. `${DB_URL}/payments/xyz.json`)
 * @param {object} [options] - Standard fetch options (method, headers, body)
 * @returns {Promise<Response>} - The fetch Response object
 */
async function firebaseFetch(url, options = {}) {
  // Append auth token to URL
  if (DB_SECRET) {
    const separator = url.includes('?') ? '&' : '?';
    url = `${url}${separator}auth=${DB_SECRET}`;
  } else {
    console.warn('[_firebaseAdmin] FIREBASE_DB_SECRET not set — request will be unauthenticated');
  }

  return fetch(url, options);
}

module.exports = { firebaseFetch };
