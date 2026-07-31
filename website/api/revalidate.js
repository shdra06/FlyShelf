const crypto = require('crypto');
const jwt = require('jsonwebtoken');
const { firebaseFetch } = require('./_firebaseAdmin');

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

    if (!DB_URL) {
      console.error('[revalidate] FIREBASE_RTDB_URL not configured');
      return res.status(500).json({ valid: false, error: 'Database not configured.' });
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
    const freshToken = jwt.sign(
      {
        key: encryptKeyForJwt(key),
        keyVersion: 2,
        deviceId: tokenDeviceId,
        tier: payload.tier || 'pro',
        v: 2
      },
      JWT_SECRET,
      {
        algorithm: 'HS256',
        expiresIn: `${TOKEN_EXPIRY_DAYS}d`,
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
      expiresIn: TOKEN_EXPIRY_DAYS * 86400
    });

  } catch (err) {
    console.error('[revalidate] Error:', err);
    return res.status(500).json({ valid: false, error: 'Internal server error.' });
  }
};
