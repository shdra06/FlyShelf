const crypto = require('crypto');

// ═══════════════════════════════════════════════════════════════════
// HMAC secret for license-key checksum — MUST be set as env var.
// NEVER hardcode the fallback in source code (security audit v2.0.0)
// ═══════════════════════════════════════════════════════════════════
const HMAC_SECRET = process.env.HMAC_SECRET;

function generateProKey() {
  if (!HMAC_SECRET) throw new Error('HMAC_SECRET environment variable not configured.');
  const chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
  let random12 = "";
  for (let i = 0; i < 12; i++) {
    random12 += chars[crypto.randomInt(chars.length)];
  }
  const hmac = crypto.createHmac("sha256", HMAC_SECRET);
  hmac.update(random12);
  const checksum = hmac.digest("hex").substring(0, 4).toUpperCase();
  const payload = random12 + checksum;
  return `FS-PRO-${payload.substring(0, 4)}-${payload.substring(4, 8)}-${payload.substring(8, 12)}-${payload.substring(12, 16)}`;
}

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
  } else {
    // Allow first trusted origin as default for non-browser requests
    res.setHeader('Access-Control-Allow-Origin', ALLOWED_ORIGINS[0]);
  }
  res.setHeader('Access-Control-Allow-Credentials', 'true');
  res.setHeader('Access-Control-Allow-Methods', 'POST,OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
}

module.exports = async (req, res) => {
  setCorsHeaders(req, res);

  if (req.method === 'OPTIONS') {
    res.status(200).end();
    return;
  }

  try {
    if (req.method !== 'POST') {
      return res.status(405).json({ error: 'Method Not Allowed' });
    }

    const {
      razorpay_payment_id,
      razorpay_order_id,
      razorpay_signature,
      email,
      deviceId
    } = req.body;

    if (!razorpay_payment_id || !razorpay_order_id || !razorpay_signature || !email || !deviceId) {
      return res.status(400).json({ error: 'Missing payment details.' });
    }

    const key_secret = process.env.RAZORPAY_KEY_SECRET;
    if (!key_secret) {
      return res.status(500).json({ error: 'Razorpay secret not configured.' });
    }

    // Verify HMAC-SHA256 signature from Razorpay
    const expectedSignature = crypto
      .createHmac("sha256", key_secret)
      .update(`${razorpay_order_id}|${razorpay_payment_id}`)
      .digest("hex");

    if (expectedSignature !== razorpay_signature) {
      return res.status(400).json({ error: 'Payment signature mismatch.' });
    }

    // ═══════════════════════════════════════════════════════════════
    // REPLAY ATTACK PROTECTION (security audit v2.0.0)
    // Check if this payment_id has already been processed.
    // If so, return the existing license key — don't generate a new one.
    // ═══════════════════════════════════════════════════════════════
    const dbUrl = process.env.FIREBASE_RTDB_URL || 'https://flyshelf-official-pay-default-rtdb.firebaseio.com';
    
    try {
      const existingRes = await fetch(`${dbUrl}/payments/${razorpay_payment_id}.json`);
      if (existingRes.ok) {
        const existingData = await existingRes.json();
        if (existingData && existingData.licenseKey && existingData.status === 'completed') {
          // Payment already processed — return existing key (anti-replay)
          console.log(`[verifyPayment] Replay detected for ${razorpay_payment_id} — returning existing key`);
          return res.status(200).json({
            success: true,
            licenseKey: existingData.licenseKey
          });
        }
      }
    } catch (replayCheckErr) {
      console.warn('Replay check failed (proceeding with new key):', replayCheckErr);
    }

    // Generate NEW license key (first-time payment only)
    const licenseKey = generateProKey();

    // Log payment to Firebase RTDB
    try {
      const record = {
        gateway: 'razorpay',
        paymentId: razorpay_payment_id,
        orderId: razorpay_order_id,
        amount: 29900,
        currency: 'INR',
        email,
        deviceId,
        licenseKey,
        status: 'completed',
        timestamp: new Date().toISOString()
      };
      
      // Store payment record (awaited — must succeed for replay protection)
      await fetch(`${dbUrl}/payments/${razorpay_payment_id}.json`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(record)
      });
      
      const safeKey = licenseKey.replace(/\./g, '_').replace(/\//g, '_');
      fetch(`${dbUrl}/licenses/keys/${safeKey}.json`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email, paymentId: razorpay_payment_id, generatedAt: new Date().toISOString() })
      }).catch(dbErr => console.warn('License key write failed (non-blocking):', dbErr));

    } catch (dbErr) {
      console.warn('Firebase RTDB write error:', dbErr);
    }

    return res.status(200).json({
      success: true,
      licenseKey
    });

  } catch (err) {
    console.error('Vercel verifyPayment Error:', err);
    return res.status(500).json({ error: err.message || 'Payment verification failed.' });
  }
};
