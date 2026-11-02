const crypto = require('crypto');
const { firebaseFetch, setSecurityHeaders } = require('./_firebaseAdmin');
const {
  SUBSCRIPTION_PLANS,
  validLifetimeAmounts,
  planForRazorpayPlanId,
  PAYMENT_ID_REGEX,
  SUBSCRIPTION_ID_REGEX,
  normalizeEmail,
  toSafeKey,
  fromSafeKey,
  dbGet,
  dbWrite,
  getSubscriptionRecord,
  applySubscriptionUpdate,
  issueSubscriptionLicense,
  recordSubscriptionPayment,
  claimKeyEmail,
  releaseKeyEmail,
  markEmailSent,
  revokeLicense,
  cancelRazorpaySubscription
} = require('./_billing');

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
//   Events: payment.captured,
//           subscription.authenticated, subscription.activated, subscription.charged,
//           subscription.pending, subscription.halted, subscription.paused,
//           subscription.resumed, subscription.cancelled, subscription.completed,
//           refund.processed,
//           payment.dispute.created, payment.dispute.lost, payment.dispute.won
//   Secret: (set as RAZORPAY_WEBHOOK_SECRET env var)
//
// v4.6.0: subscriptions. One FS-PRO key per subscription; renewals move
// currentPeriodEnd forward. Full refunds and disputes revoke the key of the
// lifetime or subscription payment they belong to.
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

    // ─── v4.6.0: subscription, refund and dispute events ───
    if (typeof event === 'string' && event.startsWith('subscription.')) {
      return await handleSubscriptionEvent(parsedBody, res);
    }
    if (event === 'refund.processed') {
      return await handleRefund(parsedBody, res);
    }
    if (typeof event === 'string' && event.startsWith('payment.dispute.')) {
      return await handleDispute(parsedBody, res);
    }

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

    // Subscription charges also fire payment.captured. They're handled by
    // subscription.charged (which knows the subscription), so acknowledge and stop.
    if (payment.invoice_id || payment.subscription_id) {
      console.log(`[webhook] payment.captured for a subscription invoice (${paymentId}) — handled by subscription.charged`);
      return res.status(200).json({ status: 'ignored', reason: 'subscription_payment', paymentId });
    }

    // Validate payment amount against known lifetime amounts (₹1,999 / $50 + legacy ₹299 / $9.99).
    // Anything else isn't a lifetime purchase: acknowledge with 200 so Razorpay doesn't
    // retry it for 24 h (a 4xx here can never succeed on retry), and mint nothing.
    const VALID_AMOUNTS = validLifetimeAmounts();
    if (!VALID_AMOUNTS.includes(amount)) {
      console.warn(`[webhook] Unexpected amount: ${amount} ${currency} — not a lifetime purchase, ignoring`);
      return res.status(200).json({ status: 'ignored', reason: 'not_lifetime_amount', paymentId });
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

// ═══════════════════════════════════════════════════════════════════
// Subscription events
// ═══════════════════════════════════════════════════════════════════
const ISSUE_KEY_EVENTS = ['subscription.activated', 'subscription.charged'];

function renewsOnLabel(iso) {
  const t = Date.parse(iso || '');
  return Number.isNaN(t) ? '' : new Date(t).toLocaleDateString('en-US', { year: 'numeric', month: 'long', day: 'numeric', timeZone: 'UTC' });
}

async function handleSubscriptionEvent(parsedBody, res) {
  const event = parsedBody.event;
  const subscription = parsedBody.payload?.subscription?.entity;
  const payment = parsedBody.payload?.payment?.entity || null;
  const eventAt = Number(parsedBody.created_at) || 0;

  if (!subscription || typeof subscription.id !== 'string' || !SUBSCRIPTION_ID_REGEX.test(subscription.id)) {
    console.warn('[webhook] Subscription event without a valid subscription entity');
    return res.status(400).json({ error: 'Missing subscription entity.' });
  }
  const subId = subscription.id;
  const planInfo = planForRazorpayPlanId(subscription.plan_id);
  if (!planInfo) {
    // Not one of the FlyShelf plans (or the plan env vars are missing) — nothing to do.
    console.warn(`[webhook] ${event}: plan of ${subId.substring(0, 10)}... is not a FlyShelf plan — ignoring`);
    return res.status(200).json({ status: 'ignored', reason: 'unknown_plan', event });
  }

  const dbUrl = process.env.FIREBASE_RTDB_URL;
  if (!dbUrl) {
    console.error('[webhook] FIREBASE_RTDB_URL not configured');
    return res.status(500).json({ error: 'Database not configured.' });
  }

  const paymentId = payment && PAYMENT_ID_REGEX.test(payment.id || '') ? payment.id : '';
  let safeKey = await dbGet(dbUrl, `subscriptions_by_id/${subId}`);
  if (typeof safeKey !== 'string' || !safeKey.startsWith('FS_PRO_')) safeKey = null;

  // The webhook is the safety net for the browser path: activated/charged issue
  // the key if verifySubscription never ran (tab closed, connection lost).
  let issued = null;
  if (!safeKey) {
    if (!ISSUE_KEY_EVENTS.includes(event)) {
      console.log(`[webhook] ${event} for ${subId.substring(0, 10)}... — no key yet, nothing to update`);
      return res.status(200).json({ status: 'no_record', event });
    }
    issued = await issueSubscriptionLicense({
      dbUrl,
      subscription,
      plan: planInfo.plan,
      currency: planInfo.currency,
      paymentId,
      email: normalizeEmail(subscription.notes?.email || payment?.email || ''),
      deviceId: typeof subscription.notes?.deviceId === 'string' ? subscription.notes.deviceId : '',
      source: 'webhook'
    });
    safeKey = issued.safeKey;
    console.log(`[webhook] ${issued.created ? '✅ Issued' : 'Found'} key for ${subId.substring(0, 10)}... via ${event}`);
  }

  // Every charge (first and renewals) gets a /payments record so refunds and
  // disputes can find the key, and key recovery by email keeps working.
  if (event === 'subscription.charged' && paymentId) {
    await recordSubscriptionPayment(dbUrl, {
      paymentId,
      subId,
      plan: planInfo.plan,
      currency: payment.currency || planInfo.currency,
      amount: payment.amount,
      licenseKey: fromSafeKey(safeKey),
      email: subscription.notes?.email || payment.email || '',
      deviceId: subscription.notes?.deviceId || '',
      source: 'webhook_charge'
    });
  }

  const record = await applySubscriptionUpdate(dbUrl, safeKey, subscription, { eventName: event, eventAt, source: `webhook:${event}` });

  // Email the key if nobody has yet (the webhook issued it, or the browser path's email failed).
  if (ISSUE_KEY_EVENTS.includes(event) && !record.emailSentAt && record.email && await claimKeyEmail(dbUrl, safeKey)) {
    const details = { subscription: true, planLabel: SUBSCRIPTION_PLANS[planInfo.plan].label, renewsOn: renewsOnLabel(record.currentPeriodEnd) };
    const licenseKey = fromSafeKey(safeKey);
    try {
      let emailResult = await sendPurchaseEmail(record.email, licenseKey, paymentId || subId, details);
      if (emailResult && emailResult.error) {
        await new Promise(r => setTimeout(r, 1000));
        emailResult = await sendPurchaseEmail(record.email, licenseKey, paymentId || subId, details);
      }
      if (!emailResult?.error && !emailResult?.skipped) await markEmailSent(dbUrl, safeKey);
      else await releaseKeyEmail(dbUrl, safeKey);
      console.log('[webhook] 📧 Subscription email result:', JSON.stringify(emailResult));
    } catch (emailErr) {
      console.warn('[webhook] Subscription email failed (non-blocking):', emailErr.message);
      await releaseKeyEmail(dbUrl, safeKey);
    }
  }

  console.log(`[webhook] ${event}: ${subId.substring(0, 10)}... → ${record.status} until ${record.currentPeriodEnd}`);
  return res.status(200).json({ status: 'ok', event, subscriptionStatus: record.status });
}

// ═══════════════════════════════════════════════════════════════════
// Refunds & disputes → revoke the key of that payment
// ═══════════════════════════════════════════════════════════════════

/** Find the license key (safeKey) for a payment: lifetime or subscription charge. */
async function licenseForPayment(dbUrl, paymentId, payment) {
  const record = await dbGet(dbUrl, `payments/${paymentId}`);
  if (record && typeof record.licenseKey === 'string' && /^FS-PRO-/.test(record.licenseKey)) {
    return { safeKey: toSafeKey(record.licenseKey), subscriptionId: record.subscriptionId || '' };
  }
  // A subscription charge we never stored (e.g. a renewal webhook was missed): map via its subscription.
  const subId = payment?.subscription_id || '';
  if (SUBSCRIPTION_ID_REGEX.test(subId)) {
    const safeKey = await dbGet(dbUrl, `subscriptions_by_id/${subId}`);
    if (typeof safeKey === 'string' && safeKey.startsWith('FS_PRO_')) return { safeKey, subscriptionId: subId };
  }
  return null;
}

async function revokeForPayment(dbUrl, paymentId, payment, info) {
  const found = await licenseForPayment(dbUrl, paymentId, payment);
  if (!found) {
    console.warn(`[webhook] ${info.reason}: no license found for ${paymentId}`);
    return null;
  }
  const sub = await revokeLicense(dbUrl, found.safeKey, { ...info, paymentId });
  // Stop future charges on a subscription whose key was just revoked (immediately, not at cycle end).
  const subId = sub?.subscriptionId || found.subscriptionId;
  const priorStatus = sub ? (sub.status === 'revoked' ? sub.statusBeforeRevoke : sub.status) : '';
  if (subId && !['cancelled', 'completed', 'expired'].includes(priorStatus)) {
    try {
      await cancelRazorpaySubscription(subId, false);
      console.log(`[webhook] Cancelled subscription ${subId.substring(0, 10)}... after ${info.reason}`);
    } catch (cancelErr) {
      console.warn(`[webhook] Could not cancel subscription after ${info.reason}:`, cancelErr.message);
    }
  }
  console.log(`[webhook] 🚫 Revoked ${found.safeKey.substring(0, 15)}... (${info.reason}, ${paymentId})`);
  return found;
}

async function handleRefund(parsedBody, res) {
  const refund = parsedBody.payload?.refund?.entity;
  const payment = parsedBody.payload?.payment?.entity || null;
  const paymentId = refund?.payment_id || payment?.id || '';
  if (!PAYMENT_ID_REGEX.test(paymentId)) {
    return res.status(400).json({ error: 'Missing payment id.' });
  }
  const dbUrl = process.env.FIREBASE_RTDB_URL;
  if (!dbUrl) return res.status(500).json({ error: 'Database not configured.' });

  // Only a full refund revokes; a partial refund (a goodwill credit) keeps Pro.
  const amount = Number(payment?.amount) || 0;
  const refunded = Number(payment?.amount_refunded) || Number(refund?.amount) || 0;
  const fullRefund = payment?.refund_status === 'full' || (amount > 0 && refunded >= amount);
  if (!fullRefund) {
    console.log(`[webhook] Partial refund on ${paymentId} (${refunded}/${amount}) — key kept`);
    return res.status(200).json({ status: 'ignored', reason: 'partial_refund' });
  }
  const found = await revokeForPayment(dbUrl, paymentId, payment, { reason: 'refund', refundId: refund?.id || '' });
  return res.status(200).json({ status: found ? 'revoked' : 'no_license', paymentId });
}

async function handleDispute(parsedBody, res) {
  const event = parsedBody.event;
  const dispute = parsedBody.payload?.dispute?.entity;
  const payment = parsedBody.payload?.payment?.entity || null;
  const paymentId = dispute?.payment_id || payment?.id || '';
  if (!PAYMENT_ID_REGEX.test(paymentId)) {
    return res.status(400).json({ error: 'Missing payment id.' });
  }
  const dbUrl = process.env.FIREBASE_RTDB_URL;
  if (!dbUrl) return res.status(500).json({ error: 'Database not configured.' });

  if (event === 'payment.dispute.created' || event === 'payment.dispute.lost') {
    const found = await revokeForPayment(dbUrl, paymentId, payment, { reason: 'dispute', disputeId: dispute?.id || '', disputeEvent: event });
    return res.status(200).json({ status: found ? 'revoked' : 'no_license', paymentId });
  }

  if (event === 'payment.dispute.won') {
    // Lift the revocation only if this same dispute caused it (never undo a refund).
    const found = await licenseForPayment(dbUrl, paymentId, payment);
    if (found) {
      const revoked = await dbGet(dbUrl, `licenses/revoked/${found.safeKey}`);
      if (revoked && revoked.reason === 'dispute' && revoked.paymentId === paymentId) {
        const delRes = await firebaseFetch(`${dbUrl}/licenses/revoked/${found.safeKey}.json`, { method: 'DELETE' });
        if (!delRes.ok) throw new Error(`Firebase DELETE revoked failed (${delRes.status})`);
        const sub = await getSubscriptionRecord(dbUrl, found.safeKey);
        if (sub && sub.status === 'revoked') {
          // The subscription was cancelled when the dispute opened; access runs to currentPeriodEnd.
          await dbWrite(dbUrl, `subscriptions/${found.safeKey}`, 'PATCH', { status: 'cancelled', updatedAt: new Date().toISOString() });
        }
        console.log(`[webhook] Dispute won on ${paymentId} — revocation lifted`);
        return res.status(200).json({ status: 'reinstated', paymentId });
      }
    }
    return res.status(200).json({ status: 'ok', paymentId });
  }

  console.log(`[webhook] ${event} on ${paymentId} — no action`);
  return res.status(200).json({ status: 'ignored', event });
}

// [SECURITY FIX v2.4.0]: Disable Vercel body parser to get raw body for signature verification
module.exports.config = { api: { bodyParser: false } };
