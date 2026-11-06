// ═══════════════════════════════════════════════════════════════════
// Plan status and "stop renewal", shared by the website (manageSubscription.js,
// key + email) and the app (_appAccount.js, activation token).
// This file starts with _ so Vercel does NOT expose it as an API route.
// ═══════════════════════════════════════════════════════════════════
const {
  SUBSCRIPTION_PLANS,
  dbWrite,
  fetchRazorpaySubscription,
  cancelRazorpaySubscription,
  applySubscriptionUpdate
} = require('./_billing');

const ENDED = ['cancelled', 'completed', 'expired', 'revoked'];

function planView(record) {
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

/** Bring the stored record in line with Razorpay (best effort; the stored record is the fallback). */
async function refreshedRecord(dbUrl, safeKey, record, source) {
  if (ENDED.includes(record.status)) return record;
  try {
    const entity = await fetchRazorpaySubscription(record.subscriptionId);
    return await applySubscriptionUpdate(dbUrl, safeKey, entity, { source });
  } catch (err) {
    console.warn(`[${source}] Razorpay refresh failed (showing stored record):`, err.message);
    return record;
  }
}

/**
 * Stop renewal at the end of the paid period. Returns { status, body } for the caller to send:
 * no further charges, Pro stays on until currentPeriodEnd.
 */
async function stopRenewal(dbUrl, safeKey, record, source) {
  if (ENDED.includes(record.status) || record.cancelAtPeriodEnd) {
    return { status: 200, body: { ...planView(record), alreadyCancelled: true } };
  }
  try {
    await cancelRazorpaySubscription(record.subscriptionId, true); // { cancel_at_cycle_end: 1 }
  } catch (err) {
    console.error(`[${source}] Razorpay cancel failed:`, err.message);
    return { status: 502, body: { success: false, error: 'cancel_failed', message: 'We couldn’t reach the payment provider. Please try again in a few minutes.' } };
  }
  const nowIso = new Date().toISOString();
  await dbWrite(dbUrl, `subscriptions/${safeKey}`, 'PATCH', { cancelAtPeriodEnd: true, cancelRequestedAt: nowIso, cancelSource: source, updatedAt: nowIso });
  console.log(`[${source}] Cancel at period end requested for ${String(record.subscriptionId).substring(0, 10)}...`);
  return { status: 200, body: { ...planView({ ...record, cancelAtPeriodEnd: true }), cancelled: true } };
}

module.exports = { ENDED, planView, refreshedRecord, stopRenewal };
