const crypto = require('crypto');
const jwt = require('jsonwebtoken');
const { firebaseFetch } = require('./_firebaseAdmin');

// ═══════════════════════════════════════════════════════════════════
// Server-side license key activation (security audit v2.1.0)
// Validates HMAC checksum, checks revocation/device limits,
// returns signed JWT activation token for offline verification.
// ═══════════════════════════════════════════════════════════════════

const HMAC_SECRET = process.env.HMAC_SECRET;
const JWT_SECRET = process.env.JWT_SECRET;
const DB_URL = process.env.FIREBASE_RTDB_URL;
const MAX_DEVICES = 3;
const TOKEN_EXPIRY_DAYS = 7;

// [SECURITY FIX v2.2.0]: In-memory rate limiter to prevent brute-force key guessing
const RATE_LIMIT_WINDOW_MS = 15 * 60 * 1000; // 15 minutes
const RATE_LIMIT_MAX = 10; // max attempts per IP per window
const _rateLimitMap = new Map();

function checkRateLimit(ip) {
  const now = Date.now();
  const entry = _rateLimitMap.get(ip);
  if (!entry || (now - entry.windowStart) > RATE_LIMIT_WINDOW_MS) {
    _rateLimitMap.set(ip, { windowStart: now, count: 1 });
    return true;
  }
  entry.count++;
  if (entry.count > RATE_LIMIT_MAX) return false;
  return true;
}
// Cleanup stale entries every 30 minutes
setInterval(() => {
  const now = Date.now();
  for (const [ip, entry] of _rateLimitMap) {
    if ((now - entry.windowStart) > RATE_LIMIT_WINDOW_MS) _rateLimitMap.delete(ip);
  }
}, 30 * 60 * 1000);

// ═══ CORS — Allow browser + desktop requests ═══
const ALLOWED_ORIGINS = [
  'https://fly-shelf.vercel.app',
  'https://shdra06.github.io',
  'https://flyshelf.app'
];

function setCorsHeaders(req, res) {
  const origin = req.headers.origin || '';
  if (ALLOWED_ORIGINS.includes(origin)) {
    res.setHeader('Access-Control-Allow-Origin', origin);
  }
  // No default origin for desktop app requests (no origin header)
  res.setHeader('Access-Control-Allow-Credentials', 'true');
  res.setHeader('Access-Control-Allow-Methods', 'POST,OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
}

/**
 * Validate the HMAC checksum of a license key.
 * Key format: FS-PRO-XXXX-XXXX-XXXX-XXXX (12 random + 4 HMAC checksum)
 */
function validateKeyChecksum(key) {
  if (!key || !key.startsWith('FS-PRO-')) return false;
  const payload = key.replace('FS-PRO-', '').replace(/-/g, '');
  if (payload.length !== 16) return false;

  const randomPart = payload.substring(0, 12);
  const checksum = payload.substring(12, 16);

  const hmac = crypto.createHmac('sha256', HMAC_SECRET);
  hmac.update(randomPart);
  const expected = hmac.digest('hex').substring(0, 4).toUpperCase();

  const checksumBuf = Buffer.from(checksum.toUpperCase(), 'utf8');
  const expectedBuf = Buffer.from(expected, 'utf8');
  return checksumBuf.length === expectedBuf.length && crypto.timingSafeEqual(checksumBuf, expectedBuf);
}

module.exports = async (req, res) => {
  setCorsHeaders(req, res);

  if (req.method === 'OPTIONS') {
    return res.status(200).end();
  }

  if (req.method !== 'POST') {
    return res.status(405).json({ success: false, error: 'Method Not Allowed' });
  }

  try {
    // [SECURITY FIX v2.2.0]: Rate limit activation attempts
    const clientIp = req.headers['x-forwarded-for']?.split(',')[0]?.trim() || req.socket?.remoteAddress || 'unknown';
    if (!checkRateLimit(clientIp)) {
      console.log(`[activate] Rate limit exceeded for IP: ${clientIp.substring(0, 12)}...`);
      return res.status(429).json({ success: false, error: 'Too many activation attempts. Please try again in 15 minutes.' });
    }

    // ═══ Validate environment ═══
    if (!HMAC_SECRET || !JWT_SECRET) {
      console.error('[activate] Missing HMAC_SECRET or JWT_SECRET env vars');
      return res.status(500).json({ success: false, error: 'Server configuration error.' });
    }

    if (!DB_URL) {
      console.error('[activate] FIREBASE_RTDB_URL not configured');
      return res.status(500).json({ success: false, error: 'Database not configured.' });
    }

    const { key, deviceId } = req.body || {};

    if (!key || !deviceId) {
      return res.status(400).json({ success: false, error: 'Missing key or deviceId.' });
    }

    // Validate deviceId format
    const deviceIdRegex = /^[a-zA-Z0-9_\-]{1,128}$/;
    if (!deviceIdRegex.test(deviceId)) {
      return res.status(400).json({ success: false, error: 'Invalid deviceId format.' });
    }

    // ═══ Step 1: Validate key format + HMAC checksum ═══
    const normalizedKey = key.trim().toUpperCase();
    if (!validateKeyChecksum(normalizedKey)) {
      console.log(`[activate] Invalid key checksum: ${normalizedKey.substring(0, 11)}...`);
      return res.status(400).json({ success: false, error: 'invalid_key' });
    }

    const safeKey = normalizedKey.replace(/-/g, '_');

    // ═══ Step 1.5: Verify key was legitimately purchased ═══
    // [SECURITY FIX v2.3.0]: Blocks forged keys — even with a valid HMAC checksum,
    // the key must exist in licenses/keys/ (written by verifyPayment.js on purchase)
    // or have pre-existing activations (backwards compatibility for pre-v2.3 keys)
    try {
      const keyRes = await firebaseFetch(`${DB_URL}/licenses/keys/${safeKey}.json`);
      if (keyRes.ok) {
        const keyData = await keyRes.json();
        if (!keyData || !keyData.paymentId) {
          // Key not in purchase DB — check if it has pre-existing activations (legacy key)
          const legacyCheck = await firebaseFetch(`${DB_URL}/licenses/activations/${safeKey}.json`);
          if (legacyCheck.ok) {
            const legacyData = await legacyCheck.json();
            if (legacyData && typeof legacyData === 'object' && Object.keys(legacyData).length > 0) {
              // Pre-existing key with activations — auto-register it for future lookups
              console.log(`[activate] Legacy key with activations found — auto-registering: ${safeKey.substring(0, 15)}...`);
              await firebaseFetch(`${DB_URL}/licenses/keys/${safeKey}.json`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ paymentId: 'pre_v2.3_legacy', generatedAt: new Date().toISOString(), note: 'Auto-registered from existing activations' })
              }).catch(e => console.warn('[activate] Auto-register write failed:', e.message));
            } else {
              console.log(`[activate] Key not found in purchase DB and no activations: ${safeKey.substring(0, 15)}...`);
              return res.status(403).json({ success: false, error: 'key_not_found' });
            }
          } else {
            console.log(`[activate] Key not found and activation check failed: ${safeKey.substring(0, 15)}...`);
            return res.status(403).json({ success: false, error: 'key_not_found' });
          }
        }
      } else {
        console.log(`[activate] Purchase DB lookup failed (${keyRes.status}) — rejecting`);
        return res.status(403).json({ success: false, error: 'key_not_found' });
      }
    } catch (err) {
      // If we can't verify the key exists, fail closed — don't activate unverified keys
      console.error('[activate] Purchase DB check failed:', err.message);
      return res.status(500).json({ success: false, error: 'verification_unavailable' });
    }

    // ═══ Step 2: Check if key is revoked ═══
    try {
      const revokeRes = await firebaseFetch(`${DB_URL}/licenses/revoked/${safeKey}.json`);
      if (revokeRes.ok) {
        const revokeData = await revokeRes.json();
        if (revokeData === true || (typeof revokeData === 'object' && revokeData !== null)) {
          console.log(`[activate] Key revoked: ${safeKey}`);
          return res.status(403).json({ success: false, error: 'revoked' });
        }
      }
    } catch (err) {
      // [SECURITY FIX v2.4.0]: Fail closed — if we can't verify revocation status, reject
      console.error('[activate] Revocation check failed — blocking activation:', err.message);
      return res.status(503).json({ success: false, error: 'License verification temporarily unavailable. Please try again.' });
    }

    // ═══ Step 3: Check device limit ═══
    let existingDeviceCount = 0;
    let thisDeviceAlreadyActivated = false;
    try {
      const activationsRes = await firebaseFetch(`${DB_URL}/licenses/activations/${safeKey}.json`);
      if (activationsRes.ok) {
        const activationsData = await activationsRes.json();
        if (activationsData && typeof activationsData === 'object') {
          const deviceIds = Object.keys(activationsData);
          existingDeviceCount = deviceIds.length;
          thisDeviceAlreadyActivated = deviceIds.includes(deviceId);
        }
      }
    } catch (err) {
      // [SECURITY FIX v2.4.0]: Fail closed — if we can't verify device count, reject
      console.error('[activate] Device count check failed — blocking activation:', err.message);
      return res.status(503).json({ success: false, error: 'Device verification temporarily unavailable. Please try again.' });
    }

    // Allow if: device already activated, OR under the limit
    if (!thisDeviceAlreadyActivated && existingDeviceCount >= MAX_DEVICES) {
      console.log(`[activate] Device limit exceeded: ${existingDeviceCount}/${MAX_DEVICES} for ${safeKey}`);
      return res.status(403).json({ success: false, error: 'device_limit', maxDevices: MAX_DEVICES });
    }

    // ═══ Step 4: Record activation in Firebase ═══
    const activationTime = new Date().toISOString();
    try {
      await firebaseFetch(`${DB_URL}/licenses/activations/${safeKey}/${deviceId}.json`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          deviceId,
          activatedAt: activationTime,
          activatedVia: 'server_v2.1'
        })
      });
    } catch (err) {
      console.warn('[activate] Activation record write failed:', err.message);
      // Non-fatal — continue with token issuance
    }

    // ═══ Step 5: Generate signed JWT activation token ═══
    const token = jwt.sign(
      {
        key: normalizedKey,
        deviceId,
        tier: 'pro',
        v: 1 // token version for future migrations
      },
      JWT_SECRET,
      {
        algorithm: 'HS256',
        expiresIn: `${TOKEN_EXPIRY_DAYS}d`,
        issuer: 'flyshelf-license-server'
      }
    );

    console.log(`[activate] ✅ Key activated: ${safeKey.substring(0, 15)}... on device ${deviceId}`);

    return res.status(200).json({
      success: true,
      token,
      expiresIn: TOKEN_EXPIRY_DAYS * 86400 // seconds
    });

  } catch (err) {
    console.error('[activate] Error:', err);
    return res.status(500).json({ success: false, error: 'Internal server error.' });
  }
};
