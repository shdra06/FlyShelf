const crypto = require('crypto');
const jwt = require('jsonwebtoken');
const { firebaseFetch } = require('./_firebaseAdmin');
const { clientIpHash, hashId, isRateLimited, getSubscriptionRecord } = require('./_billing');
const { planView, refreshedRecord, stopRenewal } = require('./_planActions');

// ═══════════════════════════════════════════════════════════════════
// Account actions from the FlyShelf app (v4.6.1)
//
// POST /api/revalidate  { action, token, deviceId }
//   action 'deactivate'  → frees this PC's device slot           → { success }
//   action 'plan'        → plan, status, renewal or end date      → { success, plan, status, currentPeriodEnd, cancelAtPeriodEnd, renewsAt }
//   action 'stop_renewal'→ no more charges; Pro until period end  → same, plus cancelled: true
//
// The app's activation token proves which key and PC this is, so the app never needs the
// purchase email. An expired token is accepted: its signature is still checked.
// Served through revalidate.js: the Hobby plan allows 12 functions per deploy.
// This file starts with _ so Vercel does NOT expose it as an API route.
// ═══════════════════════════════════════════════════════════════════

const JWT_SECRET = process.env.JWT_SECRET;
const DB_URL = process.env.FIREBASE_RTDB_URL;
const IP_LIMIT = { max: 30, windowMs: 15 * 60 * 1000 };
const KEY_LIMIT = { max: 20, windowMs: 15 * 60 * 1000 };
const ACTIONS = ['deactivate', 'plan', 'stop_renewal'];

function decryptKeyFromJwt(encryptedKey) {
  const derivedKey = crypto.createHash('sha256').update(JWT_SECRET).digest();
  const [ivHex, encHex] = String(encryptedKey).split(':');
  if (!ivHex || !encHex) return encryptedKey; // legacy plaintext token (v1)
  const decipher = crypto.createDecipheriv('aes-256-cbc', derivedKey, Buffer.from(ivHex, 'hex'));
  return decipher.update(encHex, 'hex', 'utf8') + decipher.final('utf8');
}

module.exports = async (req, res) => {
  try {
    if (!JWT_SECRET || !DB_URL) return res.status(500).json({ success: false, error: 'Server configuration error.' });
    const { action, token, deviceId } = req.body || {};
    if (!ACTIONS.includes(action)) return res.status(400).json({ success: false, error: 'Invalid action.' });
    try {
      if (await isRateLimited(DB_URL, 'appAccount', clientIpHash(req), IP_LIMIT)) {
        return res.status(429).json({ success: false, error: 'rate_limited' });
      }
    } catch {
      return res.status(503).json({ success: false, error: 'Service temporarily unavailable.' });
    }

    if (typeof token !== 'string' || typeof deviceId !== 'string' || !/^[a-zA-Z0-9_\-]{1,128}$/.test(deviceId)) {
      return res.status(400).json({ success: false, error: 'Missing token or deviceId.' });
    }
    let payload;
    try {
      payload = jwt.verify(token, JWT_SECRET, { algorithms: ['HS256'], issuer: 'flyshelf-license-server', ignoreExpiration: true });
    } catch {
      return res.status(401).json({ success: false, error: 'invalid_token' });
    }
    if (payload.deviceId && payload.deviceId !== deviceId) {
      return res.status(403).json({ success: false, error: 'device_mismatch' });
    }
    let key;
    try { key = payload.keyVersion === 2 ? decryptKeyFromJwt(payload.key) : payload.key; } catch { key = ''; }
    if (!key || !/^FS-PRO-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$/.test(key)) {
      return res.status(400).json({ success: false, error: 'invalid_token' });
    }
    const safeKey = key.replace(/-/g, '_');
    try {
      if (await isRateLimited(DB_URL, 'appAccountKey', hashId(safeKey), KEY_LIMIT)) {
        return res.status(429).json({ success: false, error: 'rate_limited' });
      }
    } catch {
      return res.status(503).json({ success: false, error: 'Service temporarily unavailable.' });
    }

    if (action === 'deactivate') {
      const delRes = await firebaseFetch(`${DB_URL}/licenses/activations/${safeKey}/${deviceId}.json`, { method: 'DELETE' });
      if (!delRes.ok) return res.status(503).json({ success: false, error: 'Service temporarily unavailable.' });
      console.log(`[appAccount] Freed a device slot on ${safeKey.substring(0, 15)}...`);
      return res.status(200).json({ success: true });
    }

    // A key that was refunded or revoked has no plan to show or stop.
    const revokedRes = await firebaseFetch(`${DB_URL}/licenses/revoked/${safeKey}.json`);
    if (!revokedRes.ok) return res.status(503).json({ success: false, error: 'Service temporarily unavailable.' });
    if (await revokedRes.json()) return res.status(403).json({ success: false, error: 'revoked' });

    let record = await getSubscriptionRecord(DB_URL, safeKey);
    if (!record) {
      if (action === 'stop_renewal') return res.status(400).json({ success: false, error: 'not_a_subscription', message: 'This is a lifetime key. There’s nothing to renew.' });
      return res.status(200).json({ success: true, plan: 'lifetime', planLabel: 'Pro Lifetime', status: 'active', currentPeriodEnd: '', cancelAtPeriodEnd: false, renewsAt: '' });
    }
    record = await refreshedRecord(DB_URL, safeKey, record, 'appAccount');
    if (action === 'plan') return res.status(200).json(planView(record));

    const result = await stopRenewal(DB_URL, safeKey, record, 'app');
    return res.status(result.status).json(result.body);
  } catch (err) {
    console.error('[appAccount] Error:', err);
    return res.status(500).json({ success: false, error: 'Internal server error.' });
  }
};
