const Razorpay = require('razorpay');
const { setSecurityHeaders } = require('./_firebaseAdmin');
const {
  setCorsHeaders,
  SUBSCRIPTION_PLANS,
  razorpayPlanId,
  EMAIL_REGEX,
  normalizeEmail,
  clientIpHash,
  isRateLimited, currencyForRequest } = require('./_billing');

// ═══════════════════════════════════════════════════════════════════
// Create a Razorpay subscription for Pro Monthly / Pro Yearly (v4.6.0)
//
// POST /api/createSubscription  { email, deviceId, region: 'INR'|'USD', plan: 'pro_monthly'|'pro_yearly' }
// → { subscription_id, key_id, subscriptionId, keyId, plan, amount, currency }
//
// Prices live in the Razorpay dashboard plans (ids from env vars), never in the
// request. Checkout opens with subscription_id; verifySubscription.js issues the key.
// Lifetime keeps the one-time order flow (createOrder.js).
// ═══════════════════════════════════════════════════════════════════

const RATE_LIMIT_WINDOW_MS = 15 * 60 * 1000; // 15 minutes
const RATE_LIMIT_MAX = 10;

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
      console.error('[createSubscription] FIREBASE_RTDB_URL not configured');
      return res.status(500).json({ error: 'Database not configured.' });
    }

    // Firebase-based rate limiting (persistent across serverless cold starts) — fail closed
    try {
      if (await isRateLimited(dbUrl, 'createSubscription', clientIpHash(req), { max: RATE_LIMIT_MAX, windowMs: RATE_LIMIT_WINDOW_MS })) {
        return res.status(429).json({ error: 'Too many requests. Please try again in 15 minutes.' });
      }
    } catch (rlErr) {
      console.error('[createSubscription] Rate limit check failed — blocking:', rlErr.message);
      return res.status(503).json({ error: 'Service temporarily unavailable.' });
    }

    const { email, deviceId, region, plan } = req.body || {};
    if (!email || !deviceId) {
      return res.status(400).json({ error: 'Missing email or deviceId' });
    }
    if (typeof email !== 'string' || email.length > 254 || !EMAIL_REGEX.test(email.trim())) {
      return res.status(400).json({ error: 'Invalid email format.' });
    }
    if (typeof deviceId !== 'string' || deviceId.length > 128) {
      return res.status(400).json({ error: 'Invalid deviceId.' });
    }
    if (region && region !== 'USD' && region !== 'INR') {
      return res.status(400).json({ error: 'Invalid region.' });
    }
    if (!Object.prototype.hasOwnProperty.call(SUBSCRIPTION_PLANS, plan)) {
      return res.status(400).json({ error: 'Invalid plan.' });
    }

    const key_id = process.env.RAZORPAY_KEY_ID;
    const key_secret = process.env.RAZORPAY_KEY_SECRET;
    if (!key_id || !key_secret) {
      return res.status(500).json({ error: 'Razorpay keys not configured on server.' });
    }

    // Currency comes from the visitor's country (India → INR, elsewhere → USD), not from the page.
    const currency = currencyForRequest(req, region);
    const plan_id = razorpayPlanId(plan, currency);
    if (!plan_id) {
      console.error(`[createSubscription] Missing env ${SUBSCRIPTION_PLANS[plan].planEnv[currency]}`);
      return res.status(503).json({ error: 'plan_unavailable', message: `Monthly and yearly plans aren’t available in ${currency} yet. Lifetime works today.` });
    }

    const razorpay = new Razorpay({ key_id, key_secret });
    const normalizedEmail = normalizeEmail(email);

    const subscription = await razorpay.subscriptions.create({
      plan_id,
      total_count: SUBSCRIPTION_PLANS[plan].totalCount,
      customer_notify: 1,
      notes: {
        email: normalizedEmail,
        deviceId,
        product: `FlyShelf ${SUBSCRIPTION_PLANS[plan].label}`,
        plan,
        region: currency
      }
    });

    return res.status(200).json({
      subscription_id: subscription.id,
      key_id,
      // camelCase aliases, matching createOrder.js's response style
      subscriptionId: subscription.id,
      keyId: key_id,
      plan,
      amount: SUBSCRIPTION_PLANS[plan].amounts[currency],
      currency
    });

  } catch (err) {
    console.error('Vercel createSubscription Error:', err?.error?.description || err?.message || err);
    // Generic error message to prevent info leaks
    return res.status(500).json({ error: 'Failed to start subscription.' });
  }
};
