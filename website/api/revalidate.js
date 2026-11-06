const crypto = require('crypto');
const jwt = require('jsonwebtoken');
const appAccount = require('./_appAccount');
const { firebaseFetch, setSecurityHeaders } = require('./_firebaseAdmin');
const { getSubscriptionRecord, refreshSubscriptionIfStale, evaluateSubscriptionAccess, appPlanFields, isRateLimited, hashId, clientIpHash } = require('./_billing');

// Rate limit constants
const RATE_LIMIT_WINDOW_MS = 15 * 60 * 1000; // 15 minutes
// Per IP: loose, because many people share one IP (mobile carrier NAT, offices, colleges).
// Per key: tight, so one key can't be hammered from many addresses.
const RATE_LIMIT_MAX = 60;
const KEY_RATE_LIMIT = { max: 20, windowMs: 15 * 60 * 1000 };

// ═══════════════════════════════════════════════════════════════════
// Server-side license revalidation (security audit v2.1.0)
// Verifies existing JWT token, checks revocation status,
// issues a fresh token with extended expiry.
// Called periodically by the desktop app (~every 7 days).
// ═══════════════════════════════════════════════════════════════════

const JWT_SECRET = process.env.JWT_SECRET;
const DB_URL = process.env.FIREBASE_RTDB_URL;
const MAX_DEVICES = 3;
const TOKEN_EXPIRY_DAYS = 7;

// [SECURITY FIX v2.5.0]: Decrypt/encrypt license key from/for JWT
/** The device this token belongs to (older tokens carry none: use the request's). */
function tokenDeviceIdFor(payload, deviceId) { return payload.deviceId || deviceId; }

function decryptKeyFromJwt(encryptedKey) {
  const derivedKey = crypto.createHash('sha256').update(JWT_SECRET).digest();
  const [ivHex, encHex] = encryptedKey.split(':');
  if (!ivHex || !encHex) return encryptedKey; // legacy plaintext token (v1)
  const iv = Buffer.from(ivHex, 'hex');
  const decipher = crypto.createDecipheriv('aes-256-cbc', derivedKey, iv);
  let decrypted = decipher.update(encHex, 'hex', 'utf8');
  decrypted += decipher.final('utf8');
  return decrypted;
}

function encryptKeyForJwt(licenseKey) {
  const derivedKey = crypto.createHash('sha256').update(JWT_SECRET).digest();
  const iv = crypto.randomBytes(16);
  const cipher = crypto.createCipheriv('aes-256-cbc', derivedKey, iv);
  let encrypted = cipher.update(licenseKey, 'utf8', 'hex');
  encrypted += cipher.final('hex');
  return iv.toString('hex') + ':' + encrypted;
}

// ═══ CORS ═══
const ALLOWED_ORIGINS = [
  'https://fly-shelf.vercel.app',
  'https://shdra06.github.io',
  'https://flyshelf.app',
  'https://www.flyshelf.in',
  'https://flyshelf.in'
];

function setCorsHeaders(req, res) {
  const origin = req.headers.origin || '';
  if (ALLOWED_ORIGINS.includes(origin)) {
    res.setHeader('Access-Control-Allow-Origin', origin);
  }
  res.setHeader('Access-Control-Allow-Credentials', 'true');
  res.setHeader('Access-Control-Allow-Methods', 'POST,OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
}

module.exports = async (req, res) => {
  setCorsHeaders(req, res);
  setSecurityHeaders(res);

  if (req.method === 'OPTIONS') {
    return res.status(200).end();
  }

  if (req.method !== 'POST') {
    return res.status(405).json({ valid: false, error: 'Method Not Allowed' });
  }

  try {
    if (!JWT_SECRET) {
      console.error('[revalidate] Missing JWT_SECRET env var');
      return res.status(500).json({ valid: false, error: 'Server configuration error.' });
    }

    // The app's account actions: free this PC's slot, show the plan, stop renewal (see _appAccount.js).
    if (req.body && req.body.action) return appAccount(req, res);

    if (!DB_URL) {
      console.error('[revalidate] FIREBASE_RTDB_URL not configured');
      return res.status(500).json({ valid: false, error: 'Database not configured.' });
    }

    // [AUDIT FIX v3.8.0]: Firebase-based rate limiting (was missing from this endpoint)
    // Per-IP limit (loose; see RATE_LIMIT_MAX). Fails closed if the store can't be read.
    try {
      if (await isRateLimited(DB_URL, 'revalidate', clientIpHash(req), { max: RATE_LIMIT_MAX, windowMs: RATE_LIMIT_WINDOW_MS })) {
        return res.status(429).json({ valid: false, error: 'Too many attempts. Please try again in 15 minutes.' });
      }
    } catch (rlErr) {
      console.error('[revalidate] Rate limit check failed — blocking:', rlErr.message);
      return res.status(503).json({ valid: false, error: 'Service temporarily unavailable.' });
    }

    const { token, deviceId } = req.body || {};

    if (!token || !deviceId) {
      return res.status(400).json({ valid: false, error: 'Missing token or deviceId.' });
    }

    // ═══ Step 1: Verify JWT signature and extract payload ═══
    // [SECURITY FIX v2.2.0]: Always verify signature, even for expired tokens.
    // Previously used jwt.decode() for expired tokens which skipped signature
    // verification — allowing anyone to forge a valid token with any payload.
    let payload;
    try {
      payload = jwt.verify(token, JWT_SECRET, {
        algorithms: ['HS256'],
        issuer: 'flyshelf-license-server'
      });
    } catch (jwtErr) {
      if (jwtErr.name === 'TokenExpiredError') {
        // Token expired — verify signature but ignore expiry
        try {
          payload = jwt.verify(token, JWT_SECRET, {
            algorithms: ['HS256'],
            issuer: 'flyshelf-license-server',
            ignoreExpiration: true
          });
        } catch (innerErr) {
          console.log(`[revalidate] Expired token with invalid signature: ${innerErr.message}`);
          return res.status(401).json({ valid: false, error: 'invalid_token' });
        }
        console.log(`[revalidate] Expired token for ${payload.key?.substring(0, 11)}... — checking key validity`);
      } else {
        console.log(`[revalidate] Invalid token: ${jwtErr.message}`);
        return res.status(401).json({ valid: false, error: 'invalid_token' });
      }
    }

    const rawKey = payload.key;
    // [SECURITY FIX v2.5.0]: Decrypt key if it's an encrypted v2 token
    const key = payload.keyVersion === 2 ? decryptKeyFromJwt(rawKey) : rawKey;
    // [SECURITY FIX v2.4.0]: Validate key format from JWT to prevent path injection
    if (!key || !/^FS-PRO-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$/.test(key)) {
      return res.status(400).json({ valid: false, error: 'invalid_token' });
    }

    // [SECURITY FIX v2.2.0]: Validate deviceId matches the token's embedded deviceId.
    // Previously accepted any deviceId from the request body, allowing device-limit bypass.
    if (payload.deviceId && payload.deviceId !== deviceId) {
      console.log(`[revalidate] DeviceId mismatch: token=${payload.deviceId}, request=${deviceId}`);
      return res.status(403).json({ valid: false, error: 'device_mismatch' });
    }

    const safeKey = key.replace(/-/g, '_');
    try {
      if (await isRateLimited(DB_URL, 'revalidateKey', hashId(safeKey), KEY_RATE_LIMIT)) {
        return res.status(429).json({ valid: false, error: 'rate_limited', message: 'Too many attempts for this key. Please try again in 15 minutes.' });
      }
    } catch (rlErr) {
      console.error('[revalidate] Key rate limit check failed — blocking:', rlErr.message);
      return res.status(503).json({ valid: false, error: 'Service temporarily unavailable.' });
    }

    // ═══ Step 2: Check if key is revoked ═══
    try {
      const revokeRes = await firebaseFetch(`${DB_URL}/licenses/revoked/${safeKey}.json`);
      if (revokeRes.ok) {
        const revokeData = await revokeRes.json();
        if (revokeData === true || (typeof revokeData === 'object' && revokeData !== null)) {
          console.log(`[revalidate] Key revoked: ${safeKey}`);
          return res.status(403).json({ valid: false, error: 'revoked' });
        }
      }
    } catch (err) {
      // [SECURITY FIX v2.4.0]: Fail closed — reject if revocation status unknown
      console.error('[revalidate] Revocation check failed — blocking revalidation:', err.message);
      return res.status(503).json({ valid: false, error: 'License verification temporarily unavailable. Please try again.' });
    }

    // ═══ Step 2.5: Subscription keys (v4.6.0) ═══
    // No /subscriptions record = lifetime key (unchanged behaviour, plan "lifetime").
    let subscription = null;
    let subscriptionAccess = null;
    try {
      subscription = await getSubscriptionRecord(DB_URL, safeKey);
    } catch (err) {
      // Fail closed — same as the revocation check
      console.error('[revalidate] Subscription lookup failed — blocking revalidation:', err.message);
      return res.status(503).json({ valid: false, error: 'License verification temporarily unavailable. Please try again.' });
    }
    if (subscription) {
      subscription = await refreshSubscriptionIfStale(DB_URL, safeKey, subscription);
      subscriptionAccess = evaluateSubscriptionAccess(subscription);
      if (subscriptionAccess.revoked) {
        return res.status(403).json({ valid: false, error: 'revoked' });
      }
      if (subscriptionAccess.expired) {
        console.log(`[revalidate] Subscription expired: ${safeKey.substring(0, 15)}... (${subscription.status}, ended ${subscription.currentPeriodEnd})`);
        return res.status(403).json({
          valid: false,
          error: 'subscription_expired',
          plan: subscription.plan,
          status: subscription.status,
          currentPeriodEnd: subscription.currentPeriodEnd || ''
        });
      }
    }

    // ═══ Step 3: Verify device is still within limit ═══
    try {
      const activationsRes = await firebaseFetch(`${DB_URL}/licenses/activations/${safeKey}.json?shallow=true`);
      if (activationsRes.ok) {
        const activationsData = await activationsRes.json();
        if (activationsData && typeof activationsData === 'object') {
          const deviceCount = Object.keys(activationsData).length;
          if (deviceCount > MAX_DEVICES) {
            console.log(`[revalidate] Device limit exceeded: ${deviceCount}/${MAX_DEVICES}`);
            return res.status(403).json({ valid: false, error: 'device_limit' });
          }
          // This PC was signed out (from the app on another PC, or "Sign out all PCs" on the website)
          // and its slot has been taken since: it stops here instead of pushing the key over the limit.
          if (!Object.prototype.hasOwnProperty.call(activationsData, tokenDeviceIdFor(payload, deviceId)) && deviceCount >= MAX_DEVICES) {
            console.log(`[revalidate] Device removed and slots full: ${safeKey.substring(0, 15)}...`);
            return res.status(403).json({ valid: false, error: 'device_removed' });
          }
        }
      }
    } catch (err) {
      // [SECURITY FIX v2.4.0]: Fail closed — reject if device count unknown
      console.error('[revalidate] Device count check failed — blocking revalidation:', err.message);
      return res.status(503).json({ valid: false, error: 'Device verification temporarily unavailable. Please try again.' });
    }

    // ═══ Step 4: Issue fresh JWT with new expiry ═══
    // [SECURITY FIX v2.2.0]: Use token's deviceId, not request body, to prevent injection
    const tokenDeviceId = payload.deviceId || deviceId;
    // Lifetime: 7 days. Subscription: min(7 days, currentPeriodEnd + 3 days grace).
    const planFields = appPlanFields(subscription);
    const expiresInSeconds = subscription
      ? Math.max(60, Math.round((subscriptionAccess.tokenExpiresAtMs - Date.now()) / 1000))
      : TOKEN_EXPIRY_DAYS * 86400;
    const freshToken = jwt.sign(
      {
        key: encryptKeyForJwt(key),
        keyVersion: 2,
        deviceId: tokenDeviceId,
        tier: payload.tier || 'pro',
        ...planFields,
        v: 2
      },
      JWT_SECRET,
      {
        algorithm: 'HS256',
        expiresIn: expiresInSeconds, // number = seconds
        issuer: 'flyshelf-license-server'
      }
    );

    // Update activation timestamp in Firebase
    try {
      await firebaseFetch(`${DB_URL}/licenses/activations/${safeKey}/${deviceId}.json`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          lastRevalidated: new Date().toISOString()
        })
      });
    } catch (err) {
      // Non-fatal
    }

    console.log(`[revalidate] ✅ Token refreshed for ${safeKey.substring(0, 15)}...`);

    return res.status(200).json({
      valid: true,
      token: freshToken,
      expiresIn: expiresInSeconds,
      ...planFields
    });

  } catch (err) {
    console.error('[revalidate] Error:', err);
    return res.status(500).json({ valid: false, error: 'Internal server error.' });
  }
};
