const crypto = require('crypto');
const { setSecurityHeaders } = require('./_firebaseAdmin');
const {
  setCorsHeaders,
  SUBSCRIPTION_PLANS,
  EMAIL_REGEX,
  normalizeEmail,
  toSafeKey,
  validateKeyChecksum,
  safeEqual,
  clientIpHash,
  isRateLimited,
  dbGet,
  dbWrite,
  getSubscriptionRecord
} = require('./_billing');
const { ENDED, planView: view, refreshedRecord, stopRenewal } = require('./_planActions');

// ═══════════════════════════════════════════════════════════════════
// Manage a FlyShelf Pro subscription (v4.6.0)
//
// POST /api/manageSubscription  { key, email, action?: 'status' | 'cancel' | 'reset_devices' }
// Both the license key and the purchase email must match the record.
//
// → { success, plan, planLabel, status, currentPeriodEnd, cancelAtPeriodEnd, renewsAt }
//   cancel → razorpay subscription cancel with { cancel_at_cycle_end: 1 }:
//   no further charges, Pro stays on until currentPeriodEnd.
//
//   reset_devices → signs every PC out of this key (lifetime or subscription), so a
//   lost or replaced PC stops holding one of the 3 device slots. Once per 30 days.
//
// Unknown key, wrong email and malformed key all get the same 404, after the
// same delay, so the endpoint can't be used to test keys or emails.
// ═══════════════════════════════════════════════════════════════════

const IP_LIMIT = { max: 10, windowMs: 15 * 60 * 1000 };   // per IP, 15 minutes
const KEY_LIMIT = { max: 10, windowMs: 60 * 60 * 1000 };  // per key, 1 hour
const RESET_COOLDOWN_MS = 30 * 86400 * 1000;
const NOT_FOUND_MESSAGE = 'We couldn’t find a subscription for that key and email.';

function delay(ms) { return new Promise(resolve => setTimeout(resolve, ms)); }

/** Sign every PC out of this key. PCs still running re-claim free slots on their next check, newest first. */
async function resetDevices(dbUrl, safeKey, res, extra) {
  const last = Date.parse((await dbGet(dbUrl, `licenses/device_resets/${safeKey}`)) || '');
  if (!Number.isNaN(last) && Date.now() - last < RESET_COOLDOWN_MS) {
    const next = new Date(last + RESET_COOLDOWN_MS).toISOString();
    return res.status(429).json({ success: false, error: 'reset_cooldown', nextResetAt: next,
      message: `PCs can be signed out once every 30 days. You can do it again on ${next.slice(0, 10)}.` });
  }
  await dbWrite(dbUrl, `licenses/activations/${safeKey}`, 'DELETE', null);
  await dbWrite(dbUrl, `licenses/device_resets/${safeKey}`, 'PUT', new Date().toISOString());
  console.log(`[manageSubscription] Signed out all PCs on ${safeKey.substring(0, 15)}...`);
  return res.status(200).json({ ...extra, devicesReset: true });
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

  const notFound = async () => {
    await delay(400);
    return res.status(404).json({ success: false, error: 'not_found', message: NOT_FOUND_MESSAGE });
  };

  try {
    const dbUrl = process.env.FIREBASE_RTDB_URL;
    if (!dbUrl || !process.env.HMAC_SECRET) {
      console.error('[manageSubscription] FIREBASE_RTDB_URL or HMAC_SECRET not configured');
      return res.status(500).json({ success: false, error: 'Server configuration error.' });
    }

    // Rate limit per IP (fail closed)
    try {
      if (await isRateLimited(dbUrl, 'manageSubscription', clientIpHash(req), IP_LIMIT)) {
        return res.status(429).json({ success: false, error: 'rate_limited', message: 'Too many requests. Please try again in 15 minutes.' });
      }
    } catch (rlErr) {
      console.error('[manageSubscription] Rate limit check failed — blocking:', rlErr.message);
      return res.status(503).json({ success: false, error: 'Service temporarily unavailable.' });
    }

    const { key, email, action = 'status' } = req.body || {};
    if (action !== 'status' && action !== 'cancel' && action !== 'reset_devices') {
      return res.status(400).json({ success: false, error: 'Invalid action.' });
    }
    if (typeof email !== 'string' || email.length > 254 || !EMAIL_REGEX.test(email.trim())) {
      return res.status(400).json({ success: false, error: 'invalid_email', message: 'Enter the email you used at checkout.' });
    }
    if (typeof key !== 'string' || key.length > 64) return notFound();
    const normalizedKey = key.trim().toUpperCase();
    if (!validateKeyChecksum(normalizedKey)) return notFound();
    const safeKey = toSafeKey(normalizedKey);

    // Rate limit per key, so one key can't be paired with many guessed emails
    try {
      const keyHash = crypto.createHash('sha256').update(safeKey).digest('hex').substring(0, 16);
      if (await isRateLimited(dbUrl, 'manageSubscriptionKey', keyHash, KEY_LIMIT)) {
        return res.status(429).json({ success: false, error: 'rate_limited', message: 'Too many requests for this key. Please try again in an hour.' });
      }
    } catch (rlErr) {
      console.error('[manageSubscription] Key rate limit check failed — blocking:', rlErr.message);
      return res.status(503).json({ success: false, error: 'Service temporarily unavailable.' });
    }

    const normalizedEmail = normalizeEmail(email);
    let record = await getSubscriptionRecord(dbUrl, safeKey);

    // ─── Lifetime key: nothing to manage, but say so (both key and email must match) ───
    if (!record) {
      const keyData = await dbGet(dbUrl, `licenses/keys/${safeKey}`);
      if (!keyData || !keyData.email || !safeEqual(normalizeEmail(keyData.email), normalizedEmail)) return notFound();
      const revoked = await dbGet(dbUrl, `licenses/revoked/${safeKey}`);
      if (action === 'cancel') {
        return res.status(400).json({ success: false, error: 'not_a_subscription', message: 'This is a lifetime key. There’s nothing to cancel.' });
      }
      const lifetimeView = {
        success: true, plan: 'lifetime', planLabel: 'Pro Lifetime', status: revoked ? 'revoked' : 'active',
        currentPeriodEnd: '', cancelAtPeriodEnd: false, renewsAt: ''
      };
      if (action === 'reset_devices') return resetDevices(dbUrl, safeKey, res, lifetimeView);
      return res.status(200).json(lifetimeView);
    }

    if (!record.email || !safeEqual(normalizeEmail(record.email), normalizedEmail)) return notFound();

    // ─── Show Razorpay's current state (best-effort; the stored record is the fallback) ───
    record = await refreshedRecord(dbUrl, safeKey, record, 'manageSubscription');

    if (action === 'status') {
      return res.status(200).json(view(record));
    }
    if (action === 'reset_devices') return resetDevices(dbUrl, safeKey, res, view(record));

    // ─── Cancel at the end of the paid period ───
    const result = await stopRenewal(dbUrl, safeKey, record, 'manageSubscription');
    return res.status(result.status).json(result.body);

  } catch (err) {
    console.error('[manageSubscription] Error:', err);
    return res.status(500).json({ success: false, error: 'Internal server error.' });
  }
};
