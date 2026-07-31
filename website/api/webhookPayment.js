const crypto = require('crypto');
const { firebaseFetch, setSecurityHeaders } = require('./_firebaseAdmin');

// Safe import — if nodemailer isn't available, email is skipped (not fatal)
let sendPurchaseEmail;
try {
  sendPurchaseEmail = require('./_email').sendPurchaseEmail;
} catch (e) {
  console.warn('[webhook] _email module not available:', e.message);
  sendPurchaseEmail = async () => ({ skipped: true, reason: 'module_unavailable' });
}

// ═══════════════════════════════════════════════════════════════════
// Razorpay Webhook Handler — Server-to-Server (v2.0.0)
//
// Razorpay POSTs here DIRECTLY when payment.captured fires.
// This runs independently of the user's browser — even if the user
// loses power, closes the tab, or their internet dies, this endpoint
// still generates and stores the license key in Firebase.
//
// Setup in Razorpay Dashboard:
//   Settings → Webhooks → Add Endpoint
//   URL: https://fly-shelf.vercel.app/api/webhookPayment
//   Events: payment.captured
//   Secret: (set as RAZORPAY_WEBHOOK_SECRET env var)
// ═══════════════════════════════════════════════════════════════════

const HMAC_SECRET = process.env.HMAC_SECRET;
const WEBHOOK_SECRET = process.env.RAZORPAY_WEBHOOK_SECRET;

function generateProKey() {
  if (!HMAC_SECRET) throw new Error('HMAC_SECRET not configured.');
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

module.exports = async (req, res) => {
  // Webhooks are server-to-server — no CORS needed
  setSecurityHeaders(res);
  if (req.method !== 'POST') {
    return res.status(405).json({ error: 'Method Not Allowed' });
  }

  try {
    // ─── Step 1: Verify webhook signature ───
    if (!WEBHOOK_SECRET) {
      console.error('[webhook] RAZORPAY_WEBHOOK_SECRET not configured');
      return res.status(500).json({ error: 'Webhook secret not configured.' });
    }

    // [SECURITY FIX v2.4.0]: Read raw body for accurate signature verification
    const rawChunks = [];
    for await (const chunk of req) { rawChunks.push(chunk); }
    const rawBody = Buffer.concat(rawChunks);
    const webhookBody = rawBody.toString('utf8');
    
    const expectedSignature = crypto
      .createHmac('sha256', WEBHOOK_SECRET)
      .update(rawBody)
      .digest('hex');

    const receivedSignature = req.headers['x-razorpay-signature'];
    if (!receivedSignature) {
      console.warn('[webhook] Missing signature — rejecting');
      return res.status(400).json({ error: 'Invalid webhook signature.' });
    }
    const receivedBuffer = Buffer.from(receivedSignature, 'utf8');
    const expectedBuffer = Buffer.from(expectedSignature, 'utf8');
    if (receivedBuffer.length !== expectedBuffer.length || !crypto.timingSafeEqual(receivedBuffer, expectedBuffer)) {
      console.warn('[webhook] Signature mismatch — rejecting');
      return res.status(400).json({ error: 'Invalid webhook signature.' });
    }

    // ─── Step 2: Parse the event ───
    // Parse body from raw string (since bodyParser is disabled)
    const parsedBody = JSON.parse(webhookBody);
    const event = parsedBody.event;
    if (event !== 'payment.captured') {
      // Acknowledge but ignore non-capture events
      console.log(`[webhook] Ignoring event: ${event}`);
      return res.status(200).json({ status: 'ignored', event });
    }

    const payment = parsedBody.payload?.payment?.entity;
    if (!payment) {
      console.warn('[webhook] No payment entity in payload');
      return res.status(400).json({ error: 'Missing payment entity.' });
    }

    const paymentId = payment.id;
    // [SECURITY FIX v2.4.0]: Validate payment ID format to prevent path injection
    if (!paymentId || !/^pay_[a-zA-Z0-9]{14,}$/.test(paymentId)) {
      console.warn(`[webhook] Invalid payment ID format: ${String(paymentId).substring(0, 20)}`);
      return res.status(400).json({ error: 'Invalid payment ID format.' });
    }
    const email = payment.notes?.email || payment.email || '';
    const deviceId = payment.notes?.deviceId || '';
    const amount = payment.amount; // in paise
    const currency = payment.currency;
    const orderId = payment.order_id;

    // Validate payment amount against known valid amounts
    const VALID_AMOUNTS = [29900, 999]; // INR paise, USD cents
    if (!VALID_AMOUNTS.includes(amount)) {
      console.warn(`[webhook] Unexpected amount: ${amount} ${currency}`);
      return res.status(400).json({ error: 'Invalid payment amount.' });
    }

    console.log(`[webhook] payment.captured: ${paymentId} | ${amount / 100} ${currency}`);

    // ─── Step 3: Check if already processed (idempotency) ───
    const dbUrl = process.env.FIREBASE_RTDB_URL;
    if (!dbUrl) {
      console.error('[webhook] FIREBASE_RTDB_URL not configured');
      return res.status(500).json({ error: 'Database not configured.' });
    }

    try {
      const existingRes = await firebaseFetch(`${dbUrl}/payments/${paymentId}.json`);
      if (existingRes.ok) {
        const existingData = await existingRes.json();
        if (existingData && existingData.licenseKey && existingData.status === 'completed') {
          console.log(`[webhook] Already processed ${paymentId} — skipping`);
          return res.status(200).json({ status: 'already_processed', paymentId });
        }
      }
    } catch (checkErr) {
      console.warn('[webhook] Idempotency check failed:', checkErr.message);
    }

    // ─── Step 4: Generate license key ───
    const licenseKey = generateProKey();
    console.log(`[webhook] Generated key for payment ${paymentId}: ${licenseKey.substring(0, 11)}...`);

    // ─── Step 5: Store in Firebase ───
    const record = {
      gateway: 'razorpay',
      source: 'webhook',  // Distinguishes from frontend-triggered verification
      paymentId,
      orderId,
      amount,
      currency,
      email,
      deviceId,
      licenseKey,
      status: 'completed',
      timestamp: new Date().toISOString()
    };

    // [SECURITY FIX v2.4.0]: Atomic conditional write to prevent race conditions
    const etagRes = await firebaseFetch(`${dbUrl}/payments/${paymentId}.json`, {
      headers: { 'X-Firebase-ETag': 'true' }
    });
    const currentETag = etagRes.headers.get('etag');
    const currentData = await etagRes.json();

    // Race condition: another request already wrote
    if (currentData && currentData.licenseKey && currentData.status === 'completed') {
      console.log(`[webhook] Race condition caught — payment already processed: ${paymentId}`);
      return res.status(200).json({ status: 'already_processed', paymentId });
    }

    const writeRes = await firebaseFetch(`${dbUrl}/payments/${paymentId}.json`, {
      method: 'PUT',
      headers: {
        'Content-Type': 'application/json',
        'if-match': currentETag || 'null_etag'
      },
      body: JSON.stringify(record)
    });

    if (writeRes.status === 412) {
      // Another request won the race
      console.log(`[webhook] Atomic write failed (race lost) for ${paymentId}`);
      return res.status(200).json({ status: 'already_processed', paymentId });
    }

    if (!writeRes.ok) {
      console.error('[webhook] Critical: payment record write failed');
      return res.status(500).json({ error: 'Failed to store payment record.' });
    }

    // Also store under license keys index
    // [SECURITY FIX v2.2.0]: Match activate.js sanitization (replace dashes, not dots/slashes)
    // [FIX v3.7.0]: AWAIT this write — fire-and-forget failures cause key_not_found on activation
    const safeKey = licenseKey.replace(/-/g, '_');
    try {
      const keyWriteRes = await firebaseFetch(`${dbUrl}/licenses/keys/${safeKey}.json`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email, paymentId, generatedAt: new Date().toISOString() })
      });
      if (!keyWriteRes.ok) {
        console.error(`[webhook] License key index write failed (${keyWriteRes.status}) — retrying...`);
        await firebaseFetch(`${dbUrl}/licenses/keys/${safeKey}.json`, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ email, paymentId, generatedAt: new Date().toISOString() })
        });
      }
    } catch (keyWriteErr) {
      console.error('[webhook] License key index write FAILED:', keyWriteErr.message);
    }

    console.log(`[webhook] ✅ Key stored for ${paymentId}`);

    // ─── Send confirmation email (with 1 retry for transient failures) ───
    if (email) {
      try {
        let emailResult = await sendPurchaseEmail(email, licenseKey, paymentId);
        if (emailResult && emailResult.error) {
          console.warn('[webhook] Email attempt 1 failed, retrying in 1s...');
          await new Promise(r => setTimeout(r, 1000));
          emailResult = await sendPurchaseEmail(email, licenseKey, paymentId);
        }
        console.log(`[webhook] 📧 Email result:`, JSON.stringify(emailResult));
      } catch (emailErr) {
        console.warn('[webhook] Email send failed (non-blocking):', emailErr.message);
      }
    } else {
      console.warn('[webhook] No email in payment notes — skipping email');
    }

    // Razorpay expects 200 OK to confirm receipt
    return res.status(200).json({ status: 'ok', paymentId });

  } catch (err) {
    console.error('[webhook] Error:', err);
    // [SECURITY FIX v2.4.0]: Return 500 for transient errors so Razorpay retries
    // Only return 200 if we've confirmed the payment was already handled
    return res.status(500).json({ status: 'error', message: 'Webhook processing failed — will be retried.' });
  }
};

// [SECURITY FIX v2.4.0]: Disable Vercel body parser to get raw body for signature verification
module.exports.config = { api: { bodyParser: false } };
