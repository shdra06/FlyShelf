const { setSecurityHeaders } = require('./_firebaseAdmin');
const {
  setCorsHeaders,
  SUBSCRIPTION_PLANS,
  planForRazorpayPlanId,
  EMAIL_REGEX,
  PAYMENT_ID_REGEX,
  SUBSCRIPTION_ID_REGEX,
  normalizeEmail,
  verifyRazorpaySignature,
  clientIpHash,
  isRateLimited,
  fetchRazorpaySubscription,
  issueSubscriptionLicense,
  applySubscriptionUpdate,
  claimKeyEmail,
  releaseKeyEmail,
  markEmailSent
} = require('./_billing');

// Safe import — if nodemailer isn't available, email is skipped (not fatal)
let sendPurchaseEmail;
try {
  sendPurchaseEmail = require('./_email').sendPurchaseEmail;
} catch (e) {
  console.warn('[verifySubscription] _email module not available:', e.message);
  sendPurchaseEmail = async () => ({ skipped: true, reason: 'module_unavailable' });
}

// ═══════════════════════════════════════════════════════════════════
// Verify a Razorpay subscription checkout and issue the Pro key (v4.6.0)
//
// POST /api/verifySubscription
//   { razorpay_payment_id, razorpay_subscription_id, razorpay_signature, email, deviceId }
// → { success, licenseKey, plan, status, currentPeriodEnd, emailSent }
//
// 1. HMAC_SHA256(payment_id + "|" + subscription_id, KEY_SECRET), timingSafeEqual
// 2. Fetch the subscription from Razorpay: plan must be one of ours, status
//    authenticated/active (the first charge is taken during checkout)
// 3. Issue one FS-PRO key per subscription (atomic claim — replays and the
//    webhook get the same key), store /subscriptions/{safeKey} and
//    /subscriptions_by_id/{subscriptionId}, email the key once.
// ═══════════════════════════════════════════════════════════════════

const RATE_LIMIT_WINDOW_MS = 15 * 60 * 1000; // 15 minutes
const RATE_LIMIT_MAX = 10;
const ACCEPTED_STATUSES = ['authenticated', 'active'];

function renewsOnLabel(iso) {
  const t = Date.parse(iso || '');
  return Number.isNaN(t) ? '' : new Date(t).toLocaleDateString('en-US', { year: 'numeric', month: 'long', day: 'numeric', timeZone: 'UTC' });
}

module.exports = async (req, res) => {
  setCorsHeaders(req, res);
  setSecurityHeaders(res);

  if (req.method === 'OPTIONS') {
    return res.status(200).end();
  }

  try {
    if (req.method !== 'POST') {
      return res.status(405).json({ error: 'Method Not Allowed' });
    }

    const dbUrl = process.env.FIREBASE_RTDB_URL;
    if (!dbUrl) {
      console.error('[verifySubscription] FIREBASE_RTDB_URL not configured');
      return res.status(500).json({ error: 'Database not configured.' });
    }

    try {
      if (await isRateLimited(dbUrl, 'verifySubscription', clientIpHash(req), { max: RATE_LIMIT_MAX, windowMs: RATE_LIMIT_WINDOW_MS })) {
        return res.status(429).json({ error: 'Too many attempts. Please try again in 15 minutes.' });
      }
    } catch (rlErr) {
      console.error('[verifySubscription] Rate limit check failed — blocking:', rlErr.message);
      return res.status(503).json({ error: 'Service temporarily unavailable.' });
    }

    const {
      razorpay_payment_id,
      razorpay_subscription_id,
      razorpay_signature,
      email,
      deviceId
    } = req.body || {};

    if (!razorpay_payment_id || !razorpay_subscription_id || !razorpay_signature || !email || !deviceId) {
      return res.status(400).json({ error: 'Missing payment details.' });
    }
    // Validate Razorpay ID formats to prevent Firebase path injection
    if (typeof razorpay_payment_id !== 'string' || !PAYMENT_ID_REGEX.test(razorpay_payment_id)) {
      return res.status(400).json({ error: 'Invalid payment ID format.' });
    }
    if (typeof razorpay_subscription_id !== 'string' || !SUBSCRIPTION_ID_REGEX.test(razorpay_subscription_id)) {
      return res.status(400).json({ error: 'Invalid subscription ID format.' });
    }
    if (typeof email !== 'string' || !EMAIL_REGEX.test(email.trim())) {
      return res.status(400).json({ error: 'Invalid email format.' });
    }
    if (typeof deviceId !== 'string' || deviceId.length > 128) {
      return res.status(400).json({ error: 'Invalid deviceId.' });
    }

    const key_secret = process.env.RAZORPAY_KEY_SECRET;
    if (!key_secret) {
      return res.status(500).json({ error: 'Razorpay secret not configured.' });
    }

    // ─── Step 1: signature (subscriptions sign payment_id|subscription_id) ───
    if (!verifyRazorpaySignature(razorpay_payment_id, razorpay_subscription_id, razorpay_signature, key_secret)) {
      return res.status(400).json({ error: 'Payment signature mismatch.' });
    }

    // ─── Step 2: confirm plan + status with Razorpay (defense in depth) ───
    let subscription;
    try {
      subscription = await fetchRazorpaySubscription(razorpay_subscription_id);
    } catch (fetchErr) {
      console.error('[verifySubscription] Razorpay subscription fetch failed — blocking:', fetchErr.message);
      return res.status(502).json({ error: 'Subscription verification unavailable. Please try again.' });
    }
    if (!subscription || subscription.id !== razorpay_subscription_id) {
      return res.status(400).json({ error: 'Subscription not found.' });
    }
    const planInfo = planForRazorpayPlanId(subscription.plan_id);
    if (!planInfo) {
      console.warn(`[verifySubscription] Unknown plan_id on ${razorpay_subscription_id.substring(0, 10)}...`);
      return res.status(400).json({ error: 'Invalid subscription plan.' });
    }
    if (!ACCEPTED_STATUSES.includes(subscription.status)) {
      console.warn(`[verifySubscription] Subscription status '${subscription.status}' not accepted`);
      return res.status(400).json({ error: 'Subscription is not active yet.' });
    }

    // The email/device stored on the subscription at creation time (server-set) wins
    // over whatever this request claims.
    const customerEmail = normalizeEmail(subscription.notes?.email || email);
    const customerDevice = typeof subscription.notes?.deviceId === 'string' ? subscription.notes.deviceId : deviceId;

    // ─── Step 3: issue / fetch the key (idempotent) ───
    let issued;
    try {
      issued = await issueSubscriptionLicense({
        dbUrl,
        subscription,
        plan: planInfo.plan,
        currency: planInfo.currency,
        paymentId: razorpay_payment_id,
        email: customerEmail,
        deviceId: customerDevice,
        source: 'verifySubscription'
      });
    } catch (issueErr) {
      console.error('[verifySubscription] Key issue failed:', issueErr.message);
      return res.status(500).json({ error: 'Could not store your subscription. Your payment is safe — please retry or contact support.' });
    }

    // Bring status/currentPeriodEnd in line with what Razorpay just told us.
    let record = issued.record;
    try {
      record = await applySubscriptionUpdate(dbUrl, issued.safeKey, subscription, { source: 'verifySubscription' });
    } catch (updErr) {
      console.warn('[verifySubscription] Status update failed (non-blocking):', updErr.message);
    }
    if (issued.created) console.log(`[verifySubscription] ✅ Key issued for ${razorpay_subscription_id.substring(0, 10)}...`);
    else console.log(`[verifySubscription] Replay for ${razorpay_subscription_id.substring(0, 10)}... — returning existing key`);

    // ─── Step 4: email once (whoever issued first; retried while not yet sent) ───
    let emailSent = Boolean(record?.emailSentAt);
    if (!emailSent && customerEmail && await claimKeyEmail(dbUrl, issued.safeKey)) {
      const details = {
        subscription: true,
        planLabel: SUBSCRIPTION_PLANS[planInfo.plan].label,
        renewsOn: renewsOnLabel(record?.currentPeriodEnd)
      };
      try {
        let emailResult = await sendPurchaseEmail(customerEmail, issued.licenseKey, razorpay_payment_id, details);
        if (emailResult && emailResult.error) {
          console.warn('[verifySubscription] Email attempt 1 failed, retrying in 1s...');
          await new Promise(r => setTimeout(r, 1000));
          emailResult = await sendPurchaseEmail(customerEmail, issued.licenseKey, razorpay_payment_id, details);
        }
        if (!emailResult?.error && !emailResult?.skipped) {
          emailSent = true;
          await markEmailSent(dbUrl, issued.safeKey);
        } else {
          await releaseKeyEmail(dbUrl, issued.safeKey);
        }
      } catch (emailErr) {
        console.warn('[verifySubscription] Email send failed:', emailErr.message);
        await releaseKeyEmail(dbUrl, issued.safeKey);
      }
    }

    return res.status(200).json({
      success: true,
      licenseKey: issued.licenseKey,
      plan: planInfo.plan,
      status: record?.status || 'active',
      currentPeriodEnd: record?.currentPeriodEnd || '',
      emailSent
    });

  } catch (err) {
    console.error('Vercel verifySubscription Error:', err);
    return res.status(500).json({ error: 'Subscription verification failed.' });
  }
};
