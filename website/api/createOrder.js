const Razorpay = require('razorpay');

// [SECURITY FIX v2.2.0]: In-memory rate limiter to prevent order spam
const RATE_LIMIT_WINDOW_MS = 15 * 60 * 1000; // 15 minutes
const RATE_LIMIT_MAX = 10; // max orders per IP per window
const _rateLimitMap = new Map();

function checkRateLimit(ip) {
  const now = Date.now();
  const entry = _rateLimitMap.get(ip);
  if (!entry || (now - entry.windowStart) > RATE_LIMIT_WINDOW_MS) {
    _rateLimitMap.set(ip, { windowStart: now, count: 1 });
    return true;
  }
  entry.count++;
  if (entry.count > RATE_LIMIT_MAX) return false;
  return true;
}
setInterval(() => {
  const now = Date.now();
  for (const [ip, entry] of _rateLimitMap) {
    if ((now - entry.windowStart) > RATE_LIMIT_WINDOW_MS) _rateLimitMap.delete(ip);
  }
}, 30 * 60 * 1000);

// ═══════════════════════════════════════════════════════════════════
// CORS — Restricted to trusted origins only (security audit v2.0.0)
// ═══════════════════════════════════════════════════════════════════
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
  // Do NOT set a default origin for non-matching/non-browser requests
  res.setHeader('Access-Control-Allow-Credentials', 'true');
  res.setHeader('Access-Control-Allow-Methods', 'POST,OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
}

module.exports = async (req, res) => {
  setCorsHeaders(req, res);

  // [SECURITY FIX v2.2.0]: Handle CORS preflight (was missing)
  if (req.method === 'OPTIONS') {
    return res.status(200).end();
  }

  try {
    if (req.method !== 'POST') {
      return res.status(405).json({ error: 'Method Not Allowed' });
    }

    // [SECURITY FIX v2.2.0]: Rate limit order creation
    const clientIp = req.headers['x-forwarded-for']?.split(',')[0]?.trim() || req.socket?.remoteAddress || 'unknown';
    if (!checkRateLimit(clientIp)) {
      return res.status(429).json({ error: 'Too many requests. Please try again in 15 minutes.' });
    }

    const { email, deviceId, region } = req.body;
    if (!email || !deviceId) {
      return res.status(400).json({ error: 'Missing email or deviceId' });
    }

    // Validate email format
    const emailRegex = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
    if (!emailRegex.test(email)) {
      return res.status(400).json({ error: 'Invalid email format.' });
    }

    // Validate deviceId
    if (typeof deviceId !== 'string' || deviceId.length > 128) {
      return res.status(400).json({ error: 'Invalid deviceId.' });
    }

    // Validate region
    if (region && region !== 'USD' && region !== 'INR') {
      return res.status(400).json({ error: 'Invalid region.' });
    }

    const key_id = process.env.RAZORPAY_KEY_ID;
    const key_secret = process.env.RAZORPAY_KEY_SECRET;

    if (!key_id || !key_secret) {
      return res.status(500).json({ error: 'Razorpay keys not configured on server.' });
    }

    const razorpay = new Razorpay({ key_id, key_secret });

    const receiptId = `rcpt_${Date.now()}_${Math.random().toString(36).substring(2, 6)}`;
    
    // Determine currency and amount based on region
    const isInternational = region === 'USD';
    const currency = isInternational ? 'USD' : 'INR';
    const amount = isInternational ? 999 : 29900; // $9.99 (999 cents) or ₹299 (29900 paise)

    const order = await razorpay.orders.create({
      amount,
      currency,
      receipt: receiptId,
      notes: {
        email,
        deviceId,
        product: 'FlyShelf Pro Lifetime',
        region: currency
      }
    });

    return res.status(200).json({
      orderId: order.id,
      amount: order.amount,
      currency: order.currency,
      keyId: key_id
    });

  } catch (err) {
    console.error('Vercel createOrder Error:', err);
    // [SECURITY FIX v2.2.0]: Generic error message to prevent info leaks
    return res.status(500).json({ error: 'Failed to initiate order.' });
  }
};
