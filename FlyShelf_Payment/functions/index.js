/**
 * ⚠️ DECOMMISSIONED — Legacy Cloud Functions (Firebase)
 * 
 * This file previously contained Razorpay payment processing Cloud Functions.
 * It has been DECOMMISSIONED because:
 * 
 * 1. HMAC_SECRET was hardcoded in source code — enabling license key forgery
 * 2. CORS was set to `origin: true` — allowing any origin
 * 3. Signature comparison used `===` instead of `crypto.timingSafeEqual()` — timing attack vulnerability
 * 4. No replay attack protection (no idempotency check)
 * 5. No rate limiting
 * 
 * All payment processing has been migrated to the Vercel API endpoints:
 *   - /api/createOrder.js
 *   - /api/verifyPayment.js
 *   - /api/activate.js
 *   - /api/webhookPayment.js
 *   - /api/recoverKey.js
 * 
 * These Vercel endpoints correctly use:
 *   - Environment variables for all secrets
 *   - crypto.timingSafeEqual() for signature verification
 *   - Replay attack protection via payment record dedup
 *   - Per-IP/per-key rate limiting
 *   - Strict CORS origin whitelist
 * 
 * DO NOT REDEPLOY THIS FILE. If you need to modify payment processing,
 * edit the Vercel API files in /website/api/ instead.
 * 
 * Decommissioned: June 7, 2026 — Security Audit
 */

// No exports — this file is intentionally empty.
// The Firebase Cloud Functions deployment should be deleted via:
//   firebase functions:delete createRazorpayOrder verifyRazorpayPayment
