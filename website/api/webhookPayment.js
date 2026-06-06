const crypto = require('crypto');

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
  if (req.method !== 'POST') {
    return res.status(405).json({ error: 'Method Not Allowed' });
  }

  try {
    // ─── Step 1: Verify webhook signature ───
    if (!WEBHOOK_SECRET) {
      console.error('[webhook] RAZORPAY_WEBHOOK_SECRET not configured');
      return res.status(500).json({ error: 'Webhook secret not configured.' });
    }

    const webhookBody = JSON.stringify(req.body);
    const expectedSignature = crypto
      .createHmac('sha256', WEBHOOK_SECRET)
      .update(webhookBody)
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
    const event = req.body.event;
    if (event !== 'payment.captured') {
      // Acknowledge but ignore non-capture events
      console.log(`[webhook] Ignoring event: ${event}`);
      return res.status(200).json({ status: 'ignored', event });
    }

    const payment = req.body.payload?.payment?.entity;
    if (!payment) {
      console.warn('[webhook] No payment entity in payload');
      return res.status(400).json({ error: 'Missing payment entity.' });
    }

    const paymentId = payment.id;
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
    const dbUrl = process.env.FIREBASE_RTDB_URL || 'https://flyshelf-official-pay-default-rtdb.firebaseio.com';

    try {
      const existingRes = await fetch(`${dbUrl}/payments/${paymentId}.json`);
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

    const writeRes = await fetch(`${dbUrl}/payments/${paymentId}.json`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(record)
    });
    if (!writeRes.ok) {
      console.error('[webhook] Critical: payment record write failed');
      return res.status(500).json({ error: 'Failed to store payment record.' });
    }

    // Also store under license keys index
    const safeKey = licenseKey.replace(/\./g, '_').replace(/\//g, '_');
    fetch(`${dbUrl}/licenses/keys/${safeKey}.json`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email, paymentId, generatedAt: new Date().toISOString() })
    }).catch(err => console.warn('[webhook] License index write failed:', err.message));

    console.log(`[webhook] ✅ Key stored for ${paymentId}`);

    // Razorpay expects 200 OK to confirm receipt
    return res.status(200).json({ status: 'ok', paymentId });

  } catch (err) {
    console.error('[webhook] Error:', err);
    // Return 200 anyway to prevent Razorpay from retrying endlessly
    // (we log the error for manual investigation)
    return res.status(200).json({ status: 'error_logged' });
  }
};
