const crypto = require('crypto');

// ═══════════════════════════════════════════════════════════════════
// Device Registration API (v1.0.0)
// Registers devices in the FlyShelf RTDB and maintains device count.
// ═══════════════════════════════════════════════════════════════════

const DB_URL = process.env.FLYSHELF_RTDB_URL;
const DB_SECRET = process.env.FLYSHELF_DB_SECRET;

// Rate limit constants
const RATE_LIMIT_WINDOW_MS = 15 * 60 * 1000; // 15 minutes
const RATE_LIMIT_MAX = 10;

/**
 * Authenticated fetch wrapper for FlyShelf RTDB REST API.
 * Authenticates via auth query parameter with the database secret.
 */
async function flyshelfFetch(url, options = {}) {
  if (DB_SECRET) {
    const separator = url.includes('?') ? '&' : '?';
    url = `${url}${separator}auth=${encodeURIComponent(DB_SECRET)}`;
  } else {
    throw new Error('[_registerAdmin] FLYSHELF_DB_SECRET not set — refusing unauthenticated request.');
  }
  return fetch(url, options);
}

// ═══ Security Headers ═══
function setSecurityHeaders(res) {
  res.setHeader('X-Content-Type-Options', 'nosniff');
  res.setHeader('X-Frame-Options', 'DENY');
  res.setHeader('X-XSS-Protection', '0');
  res.setHeader('Referrer-Policy', 'strict-origin-when-cross-origin');
  res.setHeader('Content-Security-Policy', "default-src 'none'; frame-ancestors 'none'");
  res.setHeader('Strict-Transport-Security', 'max-age=63072000; includeSubDomains; preload');
  res.setHeader('Permissions-Policy', 'camera=(), microphone=(), geolocation=()');
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

module.exports = async function handler(req, res) {
  setCorsHeaders(req, res);
  setSecurityHeaders(res);

  if (req.method === 'OPTIONS') {
    return res.status(200).end();
  }

  if (req.method !== 'POST') {
    return res.status(405).json({ success: false, error: 'Method Not Allowed' });
  }

  try {
    // ═══ Validate environment ═══
    if (!DB_URL || !DB_SECRET) {
      console.error('[register] Missing FLYSHELF_RTDB_URL or FLYSHELF_DB_SECRET env vars');
      return res.status(500).json({ success: false, error: 'Server configuration error.' });
    }

    // ═══ Rate limiting ═══
    const clientIp = req.headers['x-forwarded-for']?.split(',')[0]?.trim() || req.socket?.remoteAddress || 'unknown';
    const ipHash = crypto.createHash('sha256').update(clientIp).digest('hex').substring(0, 16);
    
    try {
      const rlRes = await flyshelfFetch(`${DB_URL}/rate_limits/register/${ipHash}.json`);
      if (rlRes.ok) {
        const rlData = await rlRes.json();
        if (rlData) {
          const recentAttempts = Object.values(rlData).filter(
            ts => (Date.now() - new Date(ts).getTime()) < RATE_LIMIT_WINDOW_MS
          );
          if (recentAttempts.length >= RATE_LIMIT_MAX) {
            console.log(`[register] Rate limit exceeded for IP: ${clientIp.substring(0, 12)}...`);
            return res.status(429).json({ success: false, error: 'Too many registration attempts. Please try again in 15 minutes.' });
          }
        }
      }
    } catch (rlErr) {
      console.error('[register] Rate limit check failed:', rlErr.message);
      return res.status(503).json({ success: false, error: 'Service temporarily unavailable.' });
    }
    
    // Record this attempt (fire-and-forget)
    flyshelfFetch(`${DB_URL}/rate_limits/register/${ipHash}/${Date.now()}.json`, {
      method: 'PUT', 
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(new Date().toISOString())
    }).catch(() => {});

    // ═══ Input Validation ═══
    const { deviceId, platform, appVersion } = req.body || {};

    if (!deviceId || !platform) {
      return res.status(400).json({ success: false, error: 'Missing deviceId or platform.' });
    }

    const deviceIdRegex = /^[a-zA-Z0-9_\-]{1,128}$/;
    if (!deviceIdRegex.test(deviceId)) {
      return res.status(400).json({ success: false, error: 'Invalid deviceId format.' });
    }

    if (platform !== 'windows' && platform !== 'android') {
      return res.status(400).json({ success: false, error: 'Invalid platform.' });
    }

    if (appVersion !== undefined) {
      const versionRegex = /^[\d\.]{1,20}$/;
      if (typeof appVersion !== 'string' || !versionRegex.test(appVersion)) {
        return res.status(400).json({ success: false, error: 'Invalid appVersion format.' });
      }
    }

    // ═══ DB Logic ═══
    const deviceUrl = `${DB_URL}/devices/${deviceId}.json`;
    const deviceRes = await flyshelfFetch(deviceUrl);
    
    if (!deviceRes.ok) {
      console.error(`[register] Failed to read device data: ${deviceRes.status}`);
      return res.status(500).json({ success: false, error: 'Database read failed.' });
    }

    const deviceData = await deviceRes.json();
    const now = new Date().toISOString();

    if (deviceData !== null) {
      // Device exists
      const updateData = { lastSeen: now };
      if (appVersion) updateData.appVersion = appVersion;

      await flyshelfFetch(deviceUrl, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(updateData)
      });

      console.log(`[register] Updated existing device: ${deviceId.substring(0, 15)}...`);
      return res.status(200).json({ success: true, isNew: false });
    } else {
      // New device
      const newData = {
        platform,
        firstSeen: now,
        lastSeen: now
      };
      if (appVersion) newData.appVersion = appVersion;

      await flyshelfFetch(deviceUrl, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(newData)
      });

      // Increment stats
      await flyshelfFetch(`${DB_URL}/stats.json`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ deviceCount: { ".sv": { "increment": 1 } } })
      });

      console.log(`[register] Registered new device: ${deviceId.substring(0, 15)}...`);
      return res.status(200).json({ success: true, isNew: true });
    }

  } catch (err) {
    console.error('[register] Error:', err);
    return res.status(500).json({ success: false, error: 'Internal server error.' });
  }
};
