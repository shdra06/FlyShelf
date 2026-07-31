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
 * Authenticates via Authorization header with the database secret.
 * Throws if FIREBASE_DB_SECRET is not set (fail-closed).
 * 
 * @param {string} url - Full Firebase REST URL (e.g. `${DB_URL}/payments/xyz.json`)
 * @param {object} [options] - Standard fetch options (method, headers, body)
 * @returns {Promise<Response>} - The fetch Response object
 */
async function firebaseFetch(url, options = {}) {
  if (DB_SECRET) {
    // Firebase RTDB legacy database secrets MUST be passed as a query parameter.
    // The Authorization: Bearer header only works with Google OAuth2 access tokens,
    // NOT with legacy database secrets. Using Bearer with a legacy secret causes
    // Firebase to silently ignore the auth, resulting in unauthenticated requests
    // that get blocked by security rules on locked paths.
    const separator = url.includes('?') ? '&' : '?';
    url = `${url}${separator}auth=${encodeURIComponent(DB_SECRET)}`;
  } else {
    // [SECURITY FIX v2.4.0]: Fail closed — never allow unauthenticated Firebase access
    throw new Error('[_firebaseAdmin] FIREBASE_DB_SECRET not set — refusing unauthenticated request. Set the environment variable.');
  }

  return fetch(url, options);
}

/**
 * Sets security response headers on all API responses.
 * Call this at the start of every API handler.
 */
function setSecurityHeaders(res) {
  res.setHeader('X-Content-Type-Options', 'nosniff');
  res.setHeader('X-Frame-Options', 'DENY');
  res.setHeader('X-XSS-Protection', '0'); // Modern browsers should use CSP instead
  res.setHeader('Referrer-Policy', 'strict-origin-when-cross-origin');
  res.setHeader('Content-Security-Policy', "default-src 'none'; frame-ancestors 'none'");
  res.setHeader('Strict-Transport-Security', 'max-age=63072000; includeSubDomains; preload');
  res.setHeader('Permissions-Policy', 'camera=(), microphone=(), geolocation=()');
}

module.exports = { firebaseFetch, setSecurityHeaders };
