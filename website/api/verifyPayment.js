const crypto = require('crypto');
const { firebaseFetch, setSecurityHeaders } = require('./_firebaseAdmin');
const { isLifetimeAmount, normalizeEmail, claimPaymentEmail, releasePaymentEmail, markPaymentEmailSent } = require('./_billing');

// Safe import — if nodemailer isn't available, email is skipped (not fatal)
let sendPurchaseEmail;
try {
  sendPurchaseEmail = require('./_email').sendPurchaseEmail;
} catch (e) {
  console.warn('[verifyPayment] _email module not available:', e.message);
  sendPurchaseEmail = async () => ({ skipped: true, reason: 'module_unavailable' });
}

// Rate limit constants
const RATE_LIMIT_WINDOW_MS = 15 * 60 * 1000; // 15 minutes
const RATE_LIMIT_MAX = 10;
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
  'https://flyshelf.app',
  'https://www.flyshelf.in',
  'https://flyshelf.in'
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
  setSecurityHeaders(res);

  if (req.method === 'OPTIONS') {
    res.status(200).end();
    return;
  }

  try {
    if (req.method !== 'POST') {
      return res.status(405).json({ error: 'Method Not Allowed' });
    }

    // [SECURITY FIX v2.5.0]: Firebase-based rate limiting (persistent across serverless cold starts)
    const dbUrl = process.env.FIREBASE_RTDB_URL;
    const clientIp = req.headers['x-forwarded-for']?.split(',')[0]?.trim() || req.socket?.remoteAddress || 'unknown';
    const ipHash = crypto.createHash('sha256').update(clientIp).digest('hex').substring(0, 16);
    try {
      const rlRes = await firebaseFetch(`${dbUrl}/rate_limits/verifyPayment/${ipHash}.json`);
      if (rlRes.ok) {
        const rlData = await rlRes.json();
        if (rlData) {
          const recentAttempts = Object.values(rlData).filter(
            ts => (Date.now() - new Date(ts).getTime()) < RATE_LIMIT_WINDOW_MS
          );
          if (recentAttempts.length >= RATE_LIMIT_MAX) {
            console.log(`[verifyPayment] Rate limit exceeded for IP: ${clientIp.substring(0, 12)}...`);
            return res.status(429).json({ error: 'Too many attempts. Please try again in 15 minutes.' });
          }
        }
      }
    } catch (rlErr) {
      console.error('[verifyPayment] Rate limit check failed — blocking:', rlErr.message);
      return res.status(503).json({ error: 'Service temporarily unavailable.' });
    }
    firebaseFetch(`${dbUrl}/rate_limits/verifyPayment/${ipHash}/${Date.now()}.json`, {
      method: 'PUT', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(new Date().toISOString())
    }).catch(() => {});

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

    // [SECURITY FIX v2.4.0]: Validate Razorpay ID formats to prevent Firebase path injection
    const paymentIdRegex = /^pay_[a-zA-Z0-9]{14,}$/;
    const orderIdRegex = /^order_[a-zA-Z0-9]{14,}$/;
    if (!paymentIdRegex.test(razorpay_payment_id)) {
      return res.status(400).json({ error: 'Invalid payment ID format.' });
    }
    if (!orderIdRegex.test(razorpay_order_id)) {
      return res.status(400).json({ error: 'Invalid order ID format.' });
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

    // [SECURITY FIX v2.2.0]: Use timing-safe comparison to prevent timing attacks
    const expectedBuf = Buffer.from(expectedSignature, 'hex');
    const signatureBuf = Buffer.from(razorpay_signature, 'hex');
    if (expectedBuf.length !== signatureBuf.length || !crypto.timingSafeEqual(expectedBuf, signatureBuf)) {
      return res.status(400).json({ error: 'Payment signature mismatch.' });
    }

    // ═══════════════════════════════════════════════════════════════
    // [SECURITY FIX v2.3.0]: Re-verify payment amount via Razorpay API
    // Signature confirms the payment exists, but we also verify the captured
    // amount matches our expected prices — defense-in-depth against amount tampering.
    // ═══════════════════════════════════════════════════════════════
    // ₹1,999 / $50 lifetime, plus the old ₹299 / $9.99 for orders created before the price change
    // (see LEGACY_LIFETIME_AMOUNTS in _billing.js).
    const key_id = process.env.RAZORPAY_KEY_ID;
    try {
      const paymentFetchRes = await fetch(`https://api.razorpay.com/v1/payments/${razorpay_payment_id}`, {
        headers: {
          'Authorization': 'Basic ' + Buffer.from(`${key_id}:${key_secret}`).toString('base64')
        }
      });
      if (paymentFetchRes.ok) {
        const paymentData = await paymentFetchRes.json();
        if (paymentData.status !== 'captured') {
          console.warn(`[verifyPayment] Payment ${razorpay_payment_id} status is '${paymentData.status}', expected 'captured'`);
          return res.status(400).json({ error: 'Payment not captured.' });
        }
        if (!isLifetimeAmount(paymentData.amount, paymentData.currency)) {
          console.warn(`[verifyPayment] Payment ${razorpay_payment_id} amount ${paymentData.amount} ${paymentData.currency} is not a lifetime price`);
          return res.status(400).json({ error: 'Invalid payment amount.' });
        }
      } else {
        console.error(`[verifyPayment] Razorpay API fetch failed: ${paymentFetchRes.status} — blocking verification`);
        return res.status(502).json({ error: 'Payment amount verification unavailable. Please try again.' });
      }
    } catch (amountCheckErr) {
      console.error('[verifyPayment] Amount re-verification failed — blocking:', amountCheckErr.message);
      return res.status(502).json({ error: 'Payment amount verification unavailable. Please try again.' });
    }

    if (!dbUrl) {
      console.error('[verifyPayment] FIREBASE_RTDB_URL not configured');
      return res.status(500).json({ error: 'Database not configured.' });
    }
    const customerEmail = normalizeEmail(email);
    const paymentPath = `${dbUrl}/payments/${razorpay_payment_id}.json`;

    // ═══════════════════════════════════════════════════════════════
    // One key per payment. The page, a retry and the webhook can all arrive
    // here: the first conditional write (ETag) claims the payment, everyone
    // else gets the stored key. A key is only returned once it is stored, so
    // the buyer never holds a key that activation can't find.
    // ═══════════════════════════════════════════════════════════════
    let licenseKey = null;
    try {
      const etagRes = await firebaseFetch(paymentPath, { headers: { 'X-Firebase-ETag': 'true' } });
      if (!etagRes.ok) throw new Error(`payment read failed (${etagRes.status})`);
      const currentETag = etagRes.headers.get('etag');
      const currentData = await etagRes.json();

      if (currentData && currentData.licenseKey && currentData.status === 'completed') {
        console.log(`[verifyPayment] Already processed ${razorpay_payment_id.substring(0, 8)}... — returning stored key`);
        licenseKey = currentData.licenseKey;
      } else {
        const candidate = generateProKey();
        const writeRes = await firebaseFetch(paymentPath, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json', 'if-match': currentETag || 'null_etag' },
          body: JSON.stringify({
            gateway: 'razorpay',
            paymentId: razorpay_payment_id,
            orderId: razorpay_order_id,
            amount: 'verified',
            currency: 'verified',
            email: customerEmail,
            deviceId,
            licenseKey: candidate,
            status: 'completed',
            timestamp: new Date().toISOString()
          })
        });
        if (writeRes.status === 412) {
          const winner = await (await firebaseFetch(paymentPath)).json();
          if (!winner?.licenseKey) throw new Error('payment claim lost but no stored key');
          licenseKey = winner.licenseKey;
        } else if (!writeRes.ok) {
          throw new Error(`payment write failed (${writeRes.status})`);
        } else {
          licenseKey = candidate;
        }
      }

      // Purchase index read by activate.js (repaired on every call, never overwritten).
      const safeKey = licenseKey.replace(/-/g, '_');
      const keyPath = `${dbUrl}/licenses/keys/${safeKey}.json`;
      const indexed = await (await firebaseFetch(keyPath)).json().catch(() => null);
      if (!indexed?.paymentId) {
        const keyWriteRes = await firebaseFetch(keyPath, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ email: customerEmail, paymentId: razorpay_payment_id, generatedAt: new Date().toISOString() })
        });
        if (!keyWriteRes.ok) throw new Error(`license index write failed (${keyWriteRes.status})`);
      }
    } catch (dbErr) {
      // Nothing usable was stored: say so. The webhook (or a retry from the page) issues the key.
      console.error('[verifyPayment] Could not store the key:', dbErr.message);
      return res.status(503).json({ error: 'We couldn’t save your license key just now. Your payment is safe — it will be emailed to you shortly.' });
    }

    // ─── Email the key once (the webhook may already have done it) ───
    let emailSent = false;
    try {
      const stored = await (await firebaseFetch(paymentPath)).json().catch(() => null);
      emailSent = Boolean(stored?.emailSentAt);
      if (!emailSent && await claimPaymentEmail(dbUrl, razorpay_payment_id)) {
        let emailResult = await sendPurchaseEmail(customerEmail, licenseKey, razorpay_payment_id);
        if (emailResult && emailResult.error) {
          console.warn('[verifyPayment] Email attempt 1 failed, retrying in 1s...');
          await new Promise(r => setTimeout(r, 1000));
          emailResult = await sendPurchaseEmail(customerEmail, licenseKey, razorpay_payment_id);
        }
        if (!emailResult?.error && !emailResult?.skipped) {
          emailSent = true;
          await markPaymentEmailSent(dbUrl, razorpay_payment_id);
        } else {
          await releasePaymentEmail(dbUrl, razorpay_payment_id);
        }
        console.log('[verifyPayment] Email result:', JSON.stringify(emailResult));
      }
    } catch (emailErr) {
      console.warn('[verifyPayment] Email send failed:', emailErr.message);
      await releasePaymentEmail(dbUrl, razorpay_payment_id);
    }

    return res.status(200).json({
      success: true,
      licenseKey,
      emailSent
    });

  } catch (err) {
    console.error('Vercel verifyPayment Error:', err);
    return res.status(500).json({ error: 'Payment verification failed.' });
  }
};
