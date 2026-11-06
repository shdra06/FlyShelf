const crypto = require('crypto');
const jwt = require('jsonwebtoken');
const { firebaseFetch, setSecurityHeaders } = require('./_firebaseAdmin');
const { getSubscriptionRecord, refreshSubscriptionIfStale, evaluateSubscriptionAccess, appPlanFields, isRateLimited, hashId, clientIpHash } = require('./_billing');

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

// Rate limit constants
const RATE_LIMIT_WINDOW_MS = 15 * 60 * 1000; // 15 minutes
// Per IP: loose, because many people share one IP (mobile carrier NAT, offices, colleges).
// Per key: tight, so one key can't be hammered from many addresses.
const RATE_LIMIT_MAX = 60;
const KEY_RATE_LIMIT = { max: 10, windowMs: 15 * 60 * 1000 };

// [SECURITY FIX v2.5.0]: Encrypt license key before embedding in JWT
// JWTs are base64 (not encrypted) — without this, the key is trivially readable
function encryptKeyForJwt(licenseKey) {
  const derivedKey = crypto.createHash('sha256').update(JWT_SECRET).digest();
  const iv = crypto.randomBytes(16);
  const cipher = crypto.createCipheriv('aes-256-cbc', derivedKey, iv);
  let encrypted = cipher.update(licenseKey, 'utf8', 'hex');
  encrypted += cipher.final('hex');
  return iv.toString('hex') + ':' + encrypted;
}

// ═══ CORS — Allow browser + desktop requests ═══
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
  setSecurityHeaders(res);

  if (req.method === 'OPTIONS') {
    return res.status(200).end();
  }

  if (req.method !== 'POST') {
    return res.status(405).json({ success: false, error: 'Method Not Allowed' });
  }

  try {
    // [SECURITY FIX v2.5.0]: Firebase-based rate limiting (persistent across serverless cold starts)
    // Per-IP limit (loose; see RATE_LIMIT_MAX). Fails closed if the store can't be read.
    try {
      if (await isRateLimited(DB_URL, 'activate', clientIpHash(req), { max: RATE_LIMIT_MAX, windowMs: RATE_LIMIT_WINDOW_MS })) {
        return res.status(429).json({ success: false, error: 'Too many activation attempts. Please try again in 15 minutes.' });
      }
    } catch (rlErr) {
      console.error('[activate] Rate limit check failed — blocking:', rlErr.message);
      return res.status(503).json({ success: false, error: 'Service temporarily unavailable.' });
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
    try {
      if (await isRateLimited(DB_URL, 'activateKey', hashId(safeKey), KEY_RATE_LIMIT)) {
        return res.status(429).json({ success: false, error: 'rate_limited', message: 'Too many attempts for this key. Please try again in 15 minutes.' });
      }
    } catch (rlErr) {
      console.error('[activate] Key rate limit check failed — blocking:', rlErr.message);
      return res.status(503).json({ success: false, error: 'Service temporarily unavailable.' });
    }

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
              // [FIX v3.7.0]: Before rejecting, check if the key exists in payments/ records
              // This handles the case where licenses/keys/ write silently failed (fire-and-forget bug)
              let foundInPayments = false;
              try {
                // Search payments by licenseKey (requires orderBy index, falls back to scan)
                const paymentSearchUrl = `${DB_URL}/payments.json?orderBy="licenseKey"&equalTo="${encodeURIComponent(normalizedKey)}"&limitToLast=1`;
                const paymentSearchRes = await firebaseFetch(paymentSearchUrl);
                if (paymentSearchRes.ok) {
                  const paymentResults = await paymentSearchRes.json();
                  if (paymentResults && Object.keys(paymentResults).length > 0) {
                    const firstPayment = Object.values(paymentResults)[0];
                    if (firstPayment.status === 'completed') {
                      foundInPayments = true;
                      // Auto-register the missing key index
                      console.log(`[activate] Key found in payments but missing from keys index — auto-registering: ${safeKey.substring(0, 15)}...`);
                      await firebaseFetch(`${DB_URL}/licenses/keys/${safeKey}.json`, {
                        method: 'PUT',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ paymentId: firstPayment.paymentId || 'recovered', email: firstPayment.email || '', generatedAt: new Date().toISOString(), note: 'Auto-recovered from payments record' })
                      }).catch(e => console.warn('[activate] Auto-recover write failed:', e.message));
                    }
                  }
                }
              } catch (paymentSearchErr) {
                console.warn('[activate] Payment fallback search failed:', paymentSearchErr.message);
              }
              if (!foundInPayments) {
                console.log(`[activate] Key not found in purchase DB, activations, or payments: ${safeKey.substring(0, 15)}...`);
                return res.status(403).json({ success: false, error: 'key_not_found' });
              }
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

    // ═══ Step 2.5: Subscription keys (v4.6.0) ═══
    // No /subscriptions record = lifetime key (behaves exactly as before).
    // With a record: refuse once the paid period + 3-day grace is over and the
    // subscription isn't active; otherwise cap the token at period end + grace.
    let subscription = null;
    let subscriptionAccess = null;
    try {
      subscription = await getSubscriptionRecord(DB_URL, safeKey);
    } catch (err) {
      // Fail closed — same as the revocation check
      console.error('[activate] Subscription lookup failed — blocking activation:', err.message);
      return res.status(503).json({ success: false, error: 'License verification temporarily unavailable. Please try again.' });
    }
    if (subscription) {
      subscription = await refreshSubscriptionIfStale(DB_URL, safeKey, subscription);
      subscriptionAccess = evaluateSubscriptionAccess(subscription);
      if (subscriptionAccess.revoked) {
        return res.status(403).json({ success: false, error: 'revoked' });
      }
      if (subscriptionAccess.expired) {
        console.log(`[activate] Subscription expired: ${safeKey.substring(0, 15)}... (${subscription.status}, ended ${subscription.currentPeriodEnd})`);
        return res.status(403).json({
          success: false,
          error: 'subscription_expired',
          plan: subscription.plan,
          status: subscription.status,
          currentPeriodEnd: subscription.currentPeriodEnd || ''
        });
      }
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
    // Lifetime: 7 days, plan "lifetime". Subscription: min(7 days, currentPeriodEnd + 3 days),
    // with plan/status/currentPeriodEnd in both the claims and the JSON response.
    const planFields = appPlanFields(subscription);
    const expiresInSeconds = subscription
      ? Math.max(60, Math.round((subscriptionAccess.tokenExpiresAtMs - Date.now()) / 1000))
      : TOKEN_EXPIRY_DAYS * 86400;
    const token = jwt.sign(
      {
        key: encryptKeyForJwt(normalizedKey),
        keyVersion: 2, // marks this as encrypted format
        deviceId,
        tier: 'pro',
        ...planFields,
        v: 2 // token version for future migrations
      },
      JWT_SECRET,
      {
        algorithm: 'HS256',
        expiresIn: expiresInSeconds, // number = seconds
        issuer: 'flyshelf-license-server'
      }
    );

    console.log(`[activate] ✅ Key activated: ${safeKey.substring(0, 15)}... on device ${deviceId} (${planFields.plan})`);

    return res.status(200).json({
      success: true,
      token,
      expiresIn: expiresInSeconds, // seconds
      ...planFields
    });

  } catch (err) {
    console.error('[activate] Error:', err);
    return res.status(500).json({ success: false, error: 'Internal server error.' });
  }
};
