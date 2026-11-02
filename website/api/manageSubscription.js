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
  getSubscriptionRecord,
  fetchRazorpaySubscription,
  cancelRazorpaySubscription,
  applySubscriptionUpdate
} = require('./_billing');

// ═══════════════════════════════════════════════════════════════════
// Manage a FlyShelf Pro subscription (v4.6.0)
//
// POST /api/manageSubscription  { key, email, action?: 'status' | 'cancel' }
// Both the license key and the purchase email must match the record.
//
// → { success, plan, planLabel, status, currentPeriodEnd, cancelAtPeriodEnd, renewsAt }
//   cancel → razorpay subscription cancel with { cancel_at_cycle_end: 1 }:
//   no further charges, Pro stays on until currentPeriodEnd.
//
// Unknown key, wrong email and malformed key all get the same 404, after the
// same delay, so the endpoint can't be used to test keys or emails.
// ═══════════════════════════════════════════════════════════════════

const IP_LIMIT = { max: 10, windowMs: 15 * 60 * 1000 };   // per IP, 15 minutes
const KEY_LIMIT = { max: 10, windowMs: 60 * 60 * 1000 };  // per key, 1 hour
const ENDED = ['cancelled', 'completed', 'expired', 'revoked'];
const NOT_FOUND_MESSAGE = 'We couldn’t find a subscription for that key and email.';

function delay(ms) { return new Promise(resolve => setTimeout(resolve, ms)); }

function view(record) {
  const ended = ENDED.includes(record.status);
  const cancelAtPeriodEnd = Boolean(record.cancelAtPeriodEnd) && !ended;
  return {
    success: true,
    plan: record.plan,
    planLabel: SUBSCRIPTION_PLANS[record.plan]?.label || 'Pro',
    status: record.status,
    currentPeriodEnd: record.currentPeriodEnd || '',
    cancelAtPeriodEnd,
    renewsAt: !ended && !cancelAtPeriodEnd && record.status !== 'halted' ? (record.currentPeriodEnd || '') : ''
  };
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
    if (action !== 'status' && action !== 'cancel') {
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
      return res.status(200).json({
        success: true, plan: 'lifetime', planLabel: 'Pro Lifetime', status: revoked ? 'revoked' : 'active',
        currentPeriodEnd: '', cancelAtPeriodEnd: false, renewsAt: ''
      });
    }

    if (!record.email || !safeEqual(normalizeEmail(record.email), normalizedEmail)) return notFound();

    // ─── Show Razorpay's current state (best-effort; the stored record is the fallback) ───
    if (!ENDED.includes(record.status)) {
      try {
        const entity = await fetchRazorpaySubscription(record.subscriptionId);
        record = await applySubscriptionUpdate(dbUrl, safeKey, entity, { source: 'manageSubscription' });
      } catch (refreshErr) {
        console.warn('[manageSubscription] Razorpay refresh failed (showing stored record):', refreshErr.message);
      }
    }

    if (action === 'status') {
      return res.status(200).json(view(record));
    }

    // ─── Cancel at the end of the paid period ───
    if (ENDED.includes(record.status) || record.cancelAtPeriodEnd) {
      return res.status(200).json({ ...view(record), alreadyCancelled: true });
    }
    try {
      await cancelRazorpaySubscription(record.subscriptionId, true); // { cancel_at_cycle_end: 1 }
    } catch (cancelErr) {
      console.error('[manageSubscription] Razorpay cancel failed:', cancelErr.message);
      return res.status(502).json({ success: false, error: 'cancel_failed', message: 'We couldn’t reach the payment provider. Please try again in a few minutes.' });
    }
    const nowIso = new Date().toISOString();
    await dbWrite(dbUrl, `subscriptions/${safeKey}`, 'PATCH', { cancelAtPeriodEnd: true, cancelRequestedAt: nowIso, updatedAt: nowIso });
    record = { ...record, cancelAtPeriodEnd: true };
    console.log(`[manageSubscription] Cancel at period end requested for ${record.subscriptionId.substring(0, 10)}...`);
    return res.status(200).json({ ...view(record), cancelled: true });

  } catch (err) {
    console.error('[manageSubscription] Error:', err);
    return res.status(500).json({ success: false, error: 'Internal server error.' });
  }
};
