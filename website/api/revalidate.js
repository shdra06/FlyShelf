const jwt = require('jsonwebtoken');

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

// ═══ CORS ═══
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
    let payload;
    try {
      payload = jwt.verify(token, JWT_SECRET, {
        algorithms: ['HS256'],
        issuer: 'flyshelf-license-server'
      });
    } catch (jwtErr) {
      if (jwtErr.name === 'TokenExpiredError') {
        // Token expired but signature valid — still allow revalidation
        // (we re-issue a fresh token if the key is still valid)
        payload = jwt.decode(token);
        if (!payload) {
          return res.status(401).json({ valid: false, error: 'invalid_token' });
        }
        console.log(`[revalidate] Expired token for ${payload.key?.substring(0, 11)}... — checking key validity`);
      } else {
        console.log(`[revalidate] Invalid token: ${jwtErr.message}`);
        return res.status(401).json({ valid: false, error: 'invalid_token' });
      }
    }

    const key = payload.key;
    if (!key) {
      return res.status(400).json({ valid: false, error: 'invalid_token' });
    }

    const safeKey = key.replace(/-/g, '_');

    // ═══ Step 2: Check if key is revoked ═══
    try {
      const revokeRes = await fetch(`${DB_URL}/licenses/revoked/${safeKey}.json`);
      if (revokeRes.ok) {
        const revokeData = await revokeRes.json();
        if (revokeData === true || (typeof revokeData === 'object' && revokeData !== null)) {
          console.log(`[revalidate] Key revoked: ${safeKey}`);
          return res.status(403).json({ valid: false, error: 'revoked' });
        }
      }
    } catch (err) {
      console.warn('[revalidate] Revocation check failed (proceeding):', err.message);
    }

    // ═══ Step 3: Verify device is still within limit ═══
    try {
      const activationsRes = await fetch(`${DB_URL}/licenses/activations/${safeKey}.json?shallow=true`);
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
      console.warn('[revalidate] Device count check failed (proceeding):', err.message);
    }

    // ═══ Step 4: Issue fresh JWT with new expiry ═══
    const freshToken = jwt.sign(
      {
        key,
        deviceId,
        tier: payload.tier || 'pro',
        v: 1
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
      await fetch(`${DB_URL}/licenses/activations/${safeKey}/${deviceId}.json`, {
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
