// ═══════════════════════════════════════════════════════════════════
// Billing helpers shared by the subscription endpoints (v4.6.0)
//
// Plans, prices, Razorpay REST calls, rate limiting, license-key issuing
// and the subscription access rule used by activate.js / revalidate.js.
//
// This file starts with _ so Vercel does NOT expose it as an API route.
//
// Firebase layout (payment RTDB):
//   /subscriptions/{safeKey}            → subscription state for a license key
//   /subscriptions_by_id/{sub_xxx}      → safeKey (index + atomic claim, one key per subscription)
//   /licenses/keys/{safeKey}            → purchase index read by activate.js (paymentId required)
//   /licenses/revoked/{safeKey}         → revocation (refunds, disputes)
//   /payments/{pay_xxx}                 → every payment that carries a key (recoverKey, refunds)
// ═══════════════════════════════════════════════════════════════════

const crypto = require('crypto');
const { firebaseFetch } = require('./_firebaseAdmin');

// ═══ CORS — same allow-list as every other endpoint ═══
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

// ═══ Plans & prices (amounts in paise / cents) ═══
// Razorpay plan ids come from env vars; the amounts here are what the
// dashboard plans must be created with (used for display + sanity checks).
const SUBSCRIPTION_PLANS = {
  pro_monthly: {
    label: 'Pro Monthly',
    period: 'monthly',
    // Razorpay requires a finite number of billing cycles. 120 months = 10 years.
    totalCount: 120,
    amounts: { INR: 9900, USD: 299 },
    planEnv: { INR: 'RAZORPAY_PLAN_MONTHLY_INR', USD: 'RAZORPAY_PLAN_MONTHLY_USD' }
  },
  pro_yearly: {
    label: 'Pro Yearly',
    period: 'yearly',
    totalCount: 10,
    amounts: { INR: 79900, USD: 1999 },
    planEnv: { INR: 'RAZORPAY_PLAN_YEARLY_INR', USD: 'RAZORPAY_PLAN_YEARLY_USD' }
  }
};

// Lifetime (one-time order flow). New buyers pay ₹1,999 / $50.
const LIFETIME_AMOUNTS = { INR: 199900, USD: 5000 };
// The old ₹299 / $9.99 price. Accepted only so that orders created before the
// price change (open checkout tabs, Razorpay webhook retries for up to 24 h)
// still produce a key. createOrder.js never creates orders at this price any
// more, and order amounts are fixed server-side, so no new buyer can pay it.
// Set ACCEPT_LEGACY_LIFETIME_AMOUNTS=false once the switch-over has settled.
const LEGACY_LIFETIME_AMOUNTS = { INR: 29900, USD: 999 };

/** True when this amount, in this currency, is a lifetime price (₹1,999 / $50, plus legacy ₹299 / $9.99). */
function isLifetimeAmount(amount, currency) {
  const cur = String(currency || '').toUpperCase();
  if (LIFETIME_AMOUNTS[cur] === amount) return true;
  return process.env.ACCEPT_LEGACY_LIFETIME_AMOUNTS !== 'false' && LEGACY_LIFETIME_AMOUNTS[cur] === amount;
}

/** Which plans can be bought in this currency right now (a subscription needs its Razorpay plan id). */
function availablePlans(currency) {
  return {
    lifetime: true,
    pro_monthly: Boolean(razorpayPlanId('pro_monthly', currency)),
    pro_yearly: Boolean(razorpayPlanId('pro_yearly', currency))
  };
}

function validLifetimeAmounts() {
  const amounts = Object.values(LIFETIME_AMOUNTS);
  if (process.env.ACCEPT_LEGACY_LIFETIME_AMOUNTS !== 'false') {
    amounts.push(...Object.values(LEGACY_LIFETIME_AMOUNTS));
  }
  return amounts;
}

function razorpayPlanId(plan, currency) {
  const def = SUBSCRIPTION_PLANS[plan];
  if (!def || !def.planEnv[currency]) return '';
  return (process.env[def.planEnv[currency]] || '').trim();
}

/** Map a Razorpay plan_id back to our plan. Returns null for plans that aren't ours. */
function planForRazorpayPlanId(planId) {
  if (!planId) return null;
  for (const [plan, def] of Object.entries(SUBSCRIPTION_PLANS)) {
    for (const currency of Object.keys(def.planEnv)) {
      const configured = razorpayPlanId(plan, currency);
      if (configured && configured === planId) return { plan, currency };
    }
  }
  return null;
}

// Razorpay subscription status → our status.
// 'authenticated' means the mandate is set up and the first charge went through
// with checkout (we never pass start_at), so it counts as active.
const STATUS_MAP = {
  created: 'created',
  authenticated: 'active',
  active: 'active',
  pending: 'past_due',
  halted: 'halted',
  cancelled: 'cancelled',
  completed: 'completed',
  expired: 'expired',
  paused: 'paused'
};

const EVENT_STATUS = {
  'subscription.authenticated': 'active',
  'subscription.activated': 'active',
  'subscription.charged': 'active',
  'subscription.resumed': 'active',
  'subscription.pending': 'past_due',
  'subscription.halted': 'halted',
  'subscription.paused': 'paused',
  'subscription.cancelled': 'cancelled',
  'subscription.completed': 'completed'
};

function mapSubscriptionStatus(razorpayStatus, eventName) {
  return STATUS_MAP[razorpayStatus] || EVENT_STATUS[eventName] || 'unknown';
}

function unixToIso(seconds) {
  const n = Number(seconds);
  if (!Number.isFinite(n) || n <= 0) return '';
  return new Date(n * 1000).toISOString();
}

/** Used when Razorpay hasn't set current_end yet (status 'authenticated'). Webhooks correct it. */
function provisionalPeriodEnd(plan, fromMs = Date.now()) {
  const d = new Date(fromMs);
  if (SUBSCRIPTION_PLANS[plan]?.period === 'yearly') d.setUTCFullYear(d.getUTCFullYear() + 1);
  else d.setUTCMonth(d.getUTCMonth() + 1);
  return d.toISOString();
}

function laterIso(a, b) {
  const ta = Date.parse(a || '');
  const tb = Date.parse(b || '');
  if (Number.isNaN(ta)) return Number.isNaN(tb) ? '' : b;
  if (Number.isNaN(tb)) return a;
  return tb > ta ? b : a;
}

// ═══ Validation ═══
const EMAIL_REGEX = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
const PAYMENT_ID_REGEX = /^pay_[a-zA-Z0-9]{14,}$/;
const SUBSCRIPTION_ID_REGEX = /^sub_[a-zA-Z0-9]{14,}$/;
const KEY_REGEX = /^FS-PRO-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$/;

function normalizeEmail(email) {
  return typeof email === 'string' ? email.trim().toLowerCase() : '';
}

function toSafeKey(licenseKey) { return licenseKey.replace(/-/g, '_'); }
function fromSafeKey(safeKey) { return safeKey.replace(/_/g, '-'); }

// ═══ License keys (same format + HMAC checksum as verifyPayment.js) ═══
function generateProKey() {
  const HMAC_SECRET = process.env.HMAC_SECRET;
  if (!HMAC_SECRET) throw new Error('HMAC_SECRET environment variable not configured.');
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
  let random12 = '';
  for (let i = 0; i < 12; i++) {
    random12 += chars[crypto.randomInt(chars.length)];
  }
  const hmac = crypto.createHmac('sha256', HMAC_SECRET);
  hmac.update(random12);
  const checksum = hmac.digest('hex').substring(0, 4).toUpperCase();
  const payload = random12 + checksum;
  return `FS-PRO-${payload.substring(0, 4)}-${payload.substring(4, 8)}-${payload.substring(8, 12)}-${payload.substring(12, 16)}`;
}

function validateKeyChecksum(key) {
  const HMAC_SECRET = process.env.HMAC_SECRET;
  if (!HMAC_SECRET || !key || !KEY_REGEX.test(key)) return false;
  const payload = key.replace('FS-PRO-', '').replace(/-/g, '');
  const randomPart = payload.substring(0, 12);
  const checksum = payload.substring(12, 16);
  const expected = crypto.createHmac('sha256', HMAC_SECRET).update(randomPart).digest('hex').substring(0, 4).toUpperCase();
  const checksumBuf = Buffer.from(checksum, 'utf8');
  const expectedBuf = Buffer.from(expected, 'utf8');
  return checksumBuf.length === expectedBuf.length && crypto.timingSafeEqual(checksumBuf, expectedBuf);
}

/** Constant-time string comparison (hashes first so lengths always match). */
function safeEqual(a, b) {
  const ha = crypto.createHash('sha256').update(String(a)).digest();
  const hb = crypto.createHash('sha256').update(String(b)).digest();
  return crypto.timingSafeEqual(ha, hb);
}

/** Razorpay checkout signature: HMAC_SHA256(left + "|" + right, KEY_SECRET), compared in constant time. */
function verifyRazorpaySignature(left, right, signature, secret) {
  if (typeof signature !== 'string' || !/^[a-f0-9]{64}$/i.test(signature)) return false;
  const expected = crypto.createHmac('sha256', secret).update(`${left}|${right}`).digest('hex');
  const expectedBuf = Buffer.from(expected, 'hex');
  const signatureBuf = Buffer.from(signature, 'hex');
  return expectedBuf.length === signatureBuf.length && crypto.timingSafeEqual(expectedBuf, signatureBuf);
}

// ═══ Rate limiting (Firebase-backed, persistent across cold starts) ═══
function clientIpHash(req) {
  const clientIp = req.headers['x-forwarded-for']?.split(',')[0]?.trim() || req.socket?.remoteAddress || 'unknown';
  return crypto.createHash('sha256').update(clientIp).digest('hex').substring(0, 16);
}

/**
 * Returns true when the caller is over the limit. Throws if the store can't be
 * read — callers fail closed (503), matching the existing endpoints.
 * Records the attempt (fire-and-forget) when under the limit.
 */
async function isRateLimited(dbUrl, bucket, id, { max = 10, windowMs = 15 * 60 * 1000 } = {}) {
  const rlRes = await firebaseFetch(`${dbUrl}/rate_limits/${bucket}/${id}.json`);
  if (!rlRes.ok) throw new Error(`rate limit read failed (${rlRes.status})`);
  const rlData = await rlRes.json();
  if (rlData) {
    const recent = Object.values(rlData).filter(ts => (Date.now() - new Date(ts).getTime()) < windowMs);
    // The daily cleanup cron can't run (Vercel doesn't serve _cleanup.js), so drop this
    // bucket's expired entries here; otherwise every check reads an ever-growing list.
    const stale = Object.entries(rlData).filter(([, ts]) => Date.now() - new Date(ts).getTime() >= Math.max(windowMs, 86400000));
    if (stale.length) {
      firebaseFetch(`${dbUrl}/rate_limits/${bucket}/${id}.json`, {
        method: 'PATCH', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(Object.fromEntries(stale.slice(0, 500).map(([k]) => [k, null])))
      }).catch(() => {});
    }
    if (recent.length >= max) return true;
  }
  // Random suffix: two requests in the same millisecond must both count.
  firebaseFetch(`${dbUrl}/rate_limits/${bucket}/${id}/${Date.now()}_${crypto.randomBytes(3).toString('hex')}.json`, {
    method: 'PUT', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(new Date().toISOString())
  }).catch(() => {});
  return false;
}

// ═══ Firebase JSON helpers ═══
async function dbGet(dbUrl, path) {
  const res = await firebaseFetch(`${dbUrl}/${path}.json`);
  if (!res.ok) throw new Error(`Firebase GET ${path.split('/')[0]} failed (${res.status})`);
  return res.json();
}

async function dbWrite(dbUrl, path, method, value) {
  const res = await firebaseFetch(`${dbUrl}/${path}.json`, {
    method,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(value)
  });
  if (!res.ok) throw new Error(`Firebase ${method} ${path.split('/')[0]} failed (${res.status})`);
  return res;
}

/** PUT only when nothing is stored yet (never overwrites an existing record). */
async function dbPutIfAbsent(dbUrl, path, value) {
  const existing = await dbGet(dbUrl, path);
  if (existing !== null && existing !== undefined) return false;
  await dbWrite(dbUrl, path, 'PUT', value);
  return true;
}

// ═══ Razorpay REST ═══
// fetch/cancel go straight to the REST API (like verifyPayment's payment fetch):
// exact payloads ({ cancel_at_cycle_end: 1 }) and a hard timeout.
async function razorpayApi(method, path, body) {
  const keyId = process.env.RAZORPAY_KEY_ID;
  const keySecret = process.env.RAZORPAY_KEY_SECRET;
  if (!keyId || !keySecret) throw new Error('Razorpay keys not configured.');
  const res = await fetch(`https://api.razorpay.com/v1/${path}`, {
    method,
    headers: {
      'Authorization': 'Basic ' + Buffer.from(`${keyId}:${keySecret}`).toString('base64'),
      'Content-Type': 'application/json'
    },
    body: body ? JSON.stringify(body) : undefined,
    signal: AbortSignal.timeout(8000)
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) {
    const err = new Error(`Razorpay ${method} ${path.split('/')[0]} failed (${res.status}): ${data?.error?.description || 'unknown'}`);
    err.status = res.status;
    throw err;
  }
  return data;
}

function fetchRazorpaySubscription(subscriptionId) {
  if (!SUBSCRIPTION_ID_REGEX.test(subscriptionId)) throw new Error('Invalid subscription id');
  return razorpayApi('GET', `subscriptions/${subscriptionId}`);
}

function cancelRazorpaySubscription(subscriptionId, atCycleEnd = true) {
  if (!SUBSCRIPTION_ID_REGEX.test(subscriptionId)) throw new Error('Invalid subscription id');
  return razorpayApi('POST', `subscriptions/${subscriptionId}/cancel`, { cancel_at_cycle_end: atCycleEnd ? 1 : 0 });
}

// ═══ Subscription records ═══
async function getSubscriptionRecord(dbUrl, safeKey) {
  const record = await dbGet(dbUrl, `subscriptions/${safeKey}`);
  return record && typeof record === 'object' ? record : null;
}

/**
 * Apply the latest Razorpay subscription entity to /subscriptions/{safeKey}.
 * - currentPeriodEnd only moves forward (out-of-order webhooks can't shorten access).
 * - status is skipped when this event is older than the last one applied.
 */
async function applySubscriptionUpdate(dbUrl, safeKey, entity, { eventName = '', eventAt = 0, source = 'api' } = {}) {
  const existing = (await getSubscriptionRecord(dbUrl, safeKey)) || {};
  const status = mapSubscriptionStatus(entity?.status, eventName);
  const nowIso = new Date().toISOString();
  const patch = { updatedAt: nowIso, lastSource: source };

  const periodEnd = laterIso(existing.currentPeriodEnd, unixToIso(entity?.current_end));
  if (periodEnd) patch.currentPeriodEnd = periodEnd;

  const lastEventAt = Number(existing.lastEventAt) || 0;
  const isStale = eventAt > 0 && eventAt < lastEventAt;
  // A revoked key stays revoked; only refunds/disputes change that.
  if (!isStale && existing.status !== 'revoked' && status !== 'unknown') {
    patch.status = status;
    if (entity?.status) patch.razorpayStatus = entity.status;
    if (status === 'cancelled' || status === 'completed' || status === 'expired') patch.cancelAtPeriodEnd = false;
  }
  if (eventAt > lastEventAt) patch.lastEventAt = eventAt;
  if (entity?.paid_count !== undefined) patch.paidCount = entity.paid_count;

  await dbWrite(dbUrl, `subscriptions/${safeKey}`, 'PATCH', patch);
  return { ...existing, ...patch };
}

/**
 * Issue (or return the already-issued) license key for a Razorpay subscription.
 * Idempotent: /subscriptions_by_id/{subId} is claimed with an ETag conditional
 * write, so concurrent verifySubscription + webhook calls share one key.
 * Missing records from a half-finished earlier run are repaired on every call.
 *
 * @returns {Promise<{licenseKey, safeKey, created, record}>}
 */
async function issueSubscriptionLicense({ dbUrl, subscription, plan, currency, paymentId, email, deviceId, source }) {
  const subId = subscription.id;
  if (!SUBSCRIPTION_ID_REGEX.test(subId)) throw new Error('Invalid subscription id');
  const claimPath = `${dbUrl}/subscriptions_by_id/${subId}.json`;

  // ─── Step 1: claim the subscription id (atomic) ───
  let safeKey = null;
  let created = false;
  const etagRes = await firebaseFetch(claimPath, { headers: { 'X-Firebase-ETag': 'true' } });
  if (!etagRes.ok) throw new Error(`Firebase claim read failed (${etagRes.status})`);
  const currentETag = etagRes.headers.get('etag');
  const current = await etagRes.json();
  if (typeof current === 'string' && current.startsWith('FS_PRO_')) {
    safeKey = current;
  } else {
    const candidate = toSafeKey(generateProKey());
    const writeRes = await firebaseFetch(claimPath, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', 'if-match': currentETag || 'null_etag' },
      body: JSON.stringify(candidate)
    });
    if (writeRes.status === 412) {
      // Another request won the race — use its key.
      const winner = await dbGet(dbUrl, `subscriptions_by_id/${subId}`);
      if (typeof winner !== 'string') throw new Error('Subscription claim lost but no winner found');
      safeKey = winner;
    } else if (!writeRes.ok) {
      throw new Error(`Firebase claim write failed (${writeRes.status})`);
    } else {
      safeKey = candidate;
      created = true;
    }
  }
  const licenseKey = fromSafeKey(safeKey);
  const nowIso = new Date().toISOString();
  const normalizedEmail = normalizeEmail(email);
  const firstPaymentId = paymentId || subId;

  // ─── Step 2: subscription record (create once, then status updates) ───
  const status = mapSubscriptionStatus(subscription.status);
  await dbPutIfAbsent(dbUrl, `subscriptions/${safeKey}`, {
    subscriptionId: subId,
    plan,
    currency,
    status: status === 'unknown' || status === 'created' ? 'active' : status,
    razorpayStatus: subscription.status || '',
    currentPeriodEnd: unixToIso(subscription.current_end) || provisionalPeriodEnd(plan),
    cancelAtPeriodEnd: false,
    email: normalizedEmail,
    deviceId: deviceId || '',
    firstPaymentId,
    lastEventAt: 0,
    createdAt: nowIso,
    updatedAt: nowIso,
    source
  });

  // ─── Step 3: purchase index read by activate.js (needs paymentId) ───
  await dbPutIfAbsent(dbUrl, `licenses/keys/${safeKey}`, {
    email: normalizedEmail,
    paymentId: firstPaymentId,
    subscriptionId: subId,
    plan,
    generatedAt: nowIso
  });

  // ─── Step 4: payment record (key recovery by email + refund/dispute lookups) ───
  if (paymentId && PAYMENT_ID_REGEX.test(paymentId)) {
    await recordSubscriptionPayment(dbUrl, { paymentId, subId, plan, currency, licenseKey, email: normalizedEmail, deviceId, source });
  }

  const record = await getSubscriptionRecord(dbUrl, safeKey);
  return { licenseKey, safeKey, created, record };
}

/** /payments/{paymentId} for a subscription charge — never overwrites. */
async function recordSubscriptionPayment(dbUrl, { paymentId, subId, plan, currency, licenseKey, email, deviceId, amount, source }) {
  return dbPutIfAbsent(dbUrl, `payments/${paymentId}`, {
    gateway: 'razorpay',
    source,
    kind: 'subscription',
    paymentId,
    subscriptionId: subId,
    plan,
    amount: amount ?? 'verified',
    currency: currency || 'verified',
    email: normalizeEmail(email),
    deviceId: deviceId || '',
    licenseKey,
    status: 'completed',
    timestamp: new Date().toISOString()
  });
}

const EMAIL_CLAIM_TTL_MS = 10 * 60 * 1000;

/**
 * Atomically claim the right to send the key email (ETag conditional write), so
 * a browser verify and a webhook arriving together send it once. A claim older
 * than 10 minutes without emailSentAt (sender crashed) can be taken over.
 */
async function claimKeyEmail(dbUrl, safeKey) {
  return claimEmailAt(dbUrl, `subscriptions/${safeKey}`);
}

/** The same once-only email claim for a lifetime purchase, on /payments/{paymentId}. */
function claimPaymentEmail(dbUrl, paymentId) { return claimEmailAt(dbUrl, `payments/${paymentId}`); }
async function releasePaymentEmail(dbUrl, paymentId) {
  await firebaseFetch(`${dbUrl}/payments/${paymentId}/emailClaimedAt.json`, { method: 'DELETE' }).catch(() => {});
}
async function markPaymentEmailSent(dbUrl, paymentId) {
  await dbWrite(dbUrl, `payments/${paymentId}`, 'PATCH', { emailSentAt: new Date().toISOString() }).catch(() => {});
}

async function claimEmailAt(dbUrl, basePath) {
  const claimUrl = `${dbUrl}/${basePath}/emailClaimedAt.json`;
  try {
    const res = await firebaseFetch(claimUrl, { headers: { 'X-Firebase-ETag': 'true' } });
    if (!res.ok) return false;
    const etag = res.headers.get('etag');
    const claimedAt = await res.json();
    if (claimedAt && Date.now() - Date.parse(claimedAt) < EMAIL_CLAIM_TTL_MS) return false;
    const put = await firebaseFetch(claimUrl, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', 'if-match': etag || 'null_etag' },
      body: JSON.stringify(new Date().toISOString())
    });
    return put.ok;
  } catch {
    return false;
  }
}

/** Give the claim back after a failed send, so the next replay or webhook retries. */
async function releaseKeyEmail(dbUrl, safeKey) {
  await firebaseFetch(`${dbUrl}/subscriptions/${safeKey}/emailClaimedAt.json`, { method: 'DELETE' }).catch(() => {});
}

/** Mark that the key email went out, so replays don't resend it. */
async function markEmailSent(dbUrl, safeKey) {
  await dbWrite(dbUrl, `subscriptions/${safeKey}`, 'PATCH', { emailSentAt: new Date().toISOString() }).catch(() => {});
}

async function revokeLicense(dbUrl, safeKey, info) {
  await dbWrite(dbUrl, `licenses/revoked/${safeKey}`, 'PUT', { ...info, revokedAt: new Date().toISOString() });
  const sub = await getSubscriptionRecord(dbUrl, safeKey).catch(() => null);
  if (sub) {
    await dbWrite(dbUrl, `subscriptions/${safeKey}`, 'PATCH', {
      status: 'revoked', statusBeforeRevoke: sub.status || '', updatedAt: new Date().toISOString()
    });
  }
  return sub;
}

// ═══ Access rule (activate.js / revalidate.js) ═══
const GRACE_MS = 3 * 86400 * 1000;
const TOKEN_MAX_MS = 7 * 86400 * 1000;
const STALE_ACTIVE_TOKEN_MS = 86400 * 1000;   // active, but renewal not seen yet
const MIN_TOKEN_MS = 60 * 1000;
const REFRESH_THROTTLE_MS = 60 * 60 * 1000;

/**
 * - expired: now > currentPeriodEnd + 3 days AND status isn't 'active'.
 * - token expiry = min(7 days, currentPeriodEnd + 3 days). If the record still
 *   says 'active' past that point (a renewal we haven't heard about yet), a
 *   24-hour token is issued so the next revalidation can pick up the renewal.
 */
function evaluateSubscriptionAccess(record, nowMs = Date.now()) {
  const endMs = Date.parse(record?.currentPeriodEnd || '');
  const isActive = record?.status === 'active';
  if (record?.status === 'revoked') return { expired: true, revoked: true };
  if (Number.isNaN(endMs)) {
    return isActive ? { expired: false, tokenExpiresAtMs: nowMs + STALE_ACTIVE_TOKEN_MS } : { expired: true };
  }
  const graceEnd = endMs + GRACE_MS;
  if (nowMs > graceEnd && !isActive) return { expired: true };
  let tokenExpiresAtMs = Math.min(nowMs + TOKEN_MAX_MS, graceEnd);
  if (tokenExpiresAtMs - nowMs < MIN_TOKEN_MS) {
    tokenExpiresAtMs = isActive ? nowMs + STALE_ACTIVE_TOKEN_MS : nowMs + MIN_TOKEN_MS;
  }
  return { expired: false, tokenExpiresAtMs };
}

/**
 * Plan fields sent to the app. After "Stop renewal" Razorpay keeps the subscription
 * 'active' until the period ends; the app is told 'cancelled' so it says
 * "ends <date>" instead of "renews <date>". Pro stays on until then either way.
 */
function appPlanFields(record) {
  if (!record) return { plan: 'lifetime' };
  const status = record.status === 'active' && record.cancelAtPeriodEnd ? 'cancelled' : record.status;
  return { plan: record.plan, status, currentPeriodEnd: record.currentPeriodEnd || '' };
}

/**
 * If the paid period has ended but no renewal webhook arrived, ask Razorpay
 * (at most once an hour per key). Best-effort: any failure keeps the record.
 */
async function refreshSubscriptionIfStale(dbUrl, safeKey, record, nowMs = Date.now()) {
  try {
    const endMs = Date.parse(record.currentPeriodEnd || '');
    const due = Number.isNaN(endMs) || nowMs > endMs;
    const lastRefresh = Date.parse(record.lastRefreshAt || '') || 0;
    const refreshable = ['active', 'past_due', 'halted', 'paused'].includes(record.status);
    if (!due || !refreshable || nowMs - lastRefresh < REFRESH_THROTTLE_MS) return record;
    if (!SUBSCRIPTION_ID_REGEX.test(record.subscriptionId || '')) return record;
    await dbWrite(dbUrl, `subscriptions/${safeKey}`, 'PATCH', { lastRefreshAt: new Date(nowMs).toISOString() });
    const entity = await fetchRazorpaySubscription(record.subscriptionId);
    return await applySubscriptionUpdate(dbUrl, safeKey, entity, { source: 'refresh' });
  } catch (err) {
    console.warn('[billing] Subscription refresh failed (using stored record):', err.message);
    return record;
  }
}

/**
 * Is this key still worth handing back to its owner? 'ok', 'revoked' or 'expired'
 * (a subscription past its paid period + grace). Used by key recovery.
 */
async function keyStanding(dbUrl, safeKey, nowMs = Date.now()) {
  if (await dbGet(dbUrl, `licenses/revoked/${safeKey}`)) return 'revoked';
  const sub = await getSubscriptionRecord(dbUrl, safeKey);
  if (!sub) return 'ok';
  const access = evaluateSubscriptionAccess(sub, nowMs);
  return access.revoked ? 'revoked' : access.expired ? 'expired' : 'ok';
}

/** Short stable hash for rate-limit buckets keyed by something other than the IP. */
function hashId(value) {
  return crypto.createHash('sha256').update(String(value)).digest('hex').substring(0, 16);
}

// ═══ Currency by country ═══
// Vercel adds x-vercel-ip-country to every request. India pays in INR, everyone else in USD.
// The server decides, so a visitor can't pick the cheaper currency from another country.
// Without the header (local dev / preview servers) the page's own choice is used.
function countryOf(req) {
  const raw = (req.headers['x-vercel-ip-country'] || '').toString().trim().toUpperCase();
  return /^[A-Z]{2}$/.test(raw) ? raw : '';
}

function currencyForRequest(req, requestedRegion) {
  const country = countryOf(req);
  if (country) return country === 'IN' ? 'INR' : 'USD';
  return requestedRegion === 'INR' ? 'INR' : 'USD';
}

module.exports = {
  countryOf,
  currencyForRequest,
  ALLOWED_ORIGINS,
  setCorsHeaders,
  SUBSCRIPTION_PLANS,
  LIFETIME_AMOUNTS,
  LEGACY_LIFETIME_AMOUNTS,
  validLifetimeAmounts,
  isLifetimeAmount,
  availablePlans,
  keyStanding,
  hashId,
  claimPaymentEmail,
  releasePaymentEmail,
  markPaymentEmailSent,
  razorpayPlanId,
  planForRazorpayPlanId,
  mapSubscriptionStatus,
  unixToIso,
  provisionalPeriodEnd,
  EMAIL_REGEX,
  PAYMENT_ID_REGEX,
  SUBSCRIPTION_ID_REGEX,
  KEY_REGEX,
  normalizeEmail,
  toSafeKey,
  fromSafeKey,
  generateProKey,
  validateKeyChecksum,
  safeEqual,
  verifyRazorpaySignature,
  clientIpHash,
  isRateLimited,
  dbGet,
  dbWrite,
  dbPutIfAbsent,
  razorpayApi,
  fetchRazorpaySubscription,
  cancelRazorpaySubscription,
  getSubscriptionRecord,
  applySubscriptionUpdate,
  issueSubscriptionLicense,
  recordSubscriptionPayment,
  claimKeyEmail,
  releaseKeyEmail,
  markEmailSent,
  revokeLicense,
  GRACE_MS,
  evaluateSubscriptionAccess,
  appPlanFields,
  refreshSubscriptionIfStale
};
