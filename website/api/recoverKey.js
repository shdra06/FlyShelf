const crypto = require('crypto');
const { sendRecoveryEmail } = require('./_email');

// ═══════════════════════════════════════════════════════════════════
// Key Recovery API — Lets users retrieve their license key by email
//
// If a user paid but lost their key (power cut, browser crash, etc.),
// they can enter their email to get it back.
//
// POST /api/recoverKey  { email: "user@example.com" }
// Returns: { success: true, message: "sent to email" }
// Key is NEVER returned in the response — only emailed (security v3.0)

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

  if (req.method === 'OPTIONS') {
    return res.status(200).end();
  }

  if (req.method !== 'POST') {
    return res.status(405).json({ error: 'Method Not Allowed' });
  }

  try {
    const { email } = req.body;

    if (!email || typeof email !== 'string') {
      return res.status(400).json({ error: 'Please provide a valid email address.' });
    }

    // Stricter email validation
    const emailRegex = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
    if (!emailRegex.test(email)) {
      return res.status(400).json({ error: 'Please provide a valid email address.' });
    }

    const normalizedEmail = email.trim().toLowerCase();

    const dbUrl = process.env.FIREBASE_RTDB_URL;
    if (!dbUrl) {
      console.error('[recoverKey] FIREBASE_RTDB_URL not configured');
      return res.status(500).json({ error: 'Database not configured.' });
    }

    // Hash the email for rate limit key (don't store raw email)
    const emailHash = crypto.createHash('sha256').update(normalizedEmail).digest('hex').substring(0, 16);

    // Check rate limit: max 5 recoveries per email per 24 hours
    try {
      const rateLimitRes = await fetch(`${dbUrl}/rate_limits/recovery/${emailHash}.json`);
      if (rateLimitRes.ok) {
        const rateLimitData = await rateLimitRes.json();
        if (rateLimitData) {
          const attempts = Object.values(rateLimitData).filter(
            ts => (Date.now() - new Date(ts).getTime()) < 86400000
          );
          if (attempts.length >= 5) {
            return res.status(429).json({ error: 'Too many recovery attempts. Please try again in 24 hours.' });
          }
        }
      }
    } catch (rlErr) {
      console.warn('[recoverKey] Rate limit check failed:', rlErr.message);
    }

    // Record this attempt
    try {
      await fetch(`${dbUrl}/rate_limits/recovery/${emailHash}/${Date.now()}.json`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(new Date().toISOString())
      });
    } catch {}

    // ─── Search all payments for this email ───
    // Firebase RTDB doesn't support native queries without indexing,
    // so we use orderBy + equalTo on the email field.
    // This requires a Firebase rule: ".indexOn": ["email"] under /payments
    // [SECURITY FIX v2.2.0]: URL-encode email to prevent Firebase query injection
    const encodedEmail = encodeURIComponent(normalizedEmail);
    const searchUrl = `${dbUrl}/payments.json?orderBy="email"&equalTo="${encodedEmail}"&limitToLast=5`;

    const searchRes = await fetch(searchUrl);

    if (!searchRes.ok) {
      // If indexing isn't set up, fall back to shallow scan
      console.warn('[recoverKey] Indexed query failed, trying shallow scan');
      return await fallbackSearch(dbUrl, normalizedEmail, res);
    }

    const results = await searchRes.json();

    if (!results || Object.keys(results).length === 0) {
      // Try case-insensitive fallback
      return await fallbackSearch(dbUrl, normalizedEmail, res);
    }

    // Find the most recent completed payment
    let latestPayment = null;
    let latestTime = '';

    for (const [paymentId, record] of Object.entries(results)) {
      if (record.status === 'completed' && record.licenseKey) {
        if (!latestTime || record.timestamp > latestTime) {
          latestPayment = record;
          latestTime = record.timestamp;
        }
      }
    }

    if (!latestPayment) {
      // [SECURITY FIX v2.2.0]: Delay on 404 too, to prevent timing-based email enumeration
      await new Promise(resolve => setTimeout(resolve, 500));
      return res.status(404).json({
        error: 'No completed payment found for this email. If you just paid, please wait 2 minutes and try again.'
      });
    }

    // Artificial delay to prevent timing-based email enumeration
    await new Promise(resolve => setTimeout(resolve, 500));

    // ─── Send key via email (NEVER return in response) ───
    try {
      await sendRecoveryEmail(latestPayment.email, latestPayment.licenseKey, latestPayment.timestamp);
    } catch (emailErr) {
      console.error('[recoverKey] Email send failed:', emailErr.message);
      return res.status(500).json({ error: 'Failed to send recovery email. Please try again or contact support.' });
    }

    return res.status(200).json({
      success: true,
      message: 'Your license key has been sent to your email address. Please check your inbox (and spam folder).'
    });

  } catch (err) {
    console.error('[recoverKey] Error:', err);
    return res.status(500).json({ error: 'Recovery failed. Please contact support.' });
  }
};

// Fallback: shallow scan when Firebase indexing isn't configured
async function fallbackSearch(dbUrl, normalizedEmail, res) {
  try {
    // Get all payment IDs (shallow)
    const shallowRes = await fetch(`${dbUrl}/payments.json?shallow=true`);
    if (!shallowRes.ok) {
      return res.status(500).json({ error: 'Database unavailable.' });
    }

    const shallowData = await shallowRes.json();
    if (!shallowData) {
      return res.status(404).json({ error: 'No payments found for this email.' });
    }

    const paymentIds = Object.keys(shallowData);

    // Search through recent payments (last 50 to limit reads)
    const recentIds = paymentIds.slice(-50);
    let foundPayment = null;

    for (const pid of recentIds) {
      try {
        const pRes = await fetch(`${dbUrl}/payments/${pid}.json`);
        if (pRes.ok) {
          const pData = await pRes.json();
          if (pData &&
              pData.email &&
              pData.email.trim().toLowerCase() === normalizedEmail &&
              pData.status === 'completed' &&
              pData.licenseKey) {
            // Keep searching for the most recent one
            if (!foundPayment || pData.timestamp > foundPayment.timestamp) {
              foundPayment = pData;
            }
          }
        }
      } catch { /* skip individual errors */ }
    }

    if (!foundPayment) {
      return res.status(404).json({
        error: 'No completed payment found for this email. If you just paid, please wait 2 minutes and try again — the webhook may still be processing.'
      });
    }

    // ─── Send key via email (NEVER return in response) ───
    try {
      await sendRecoveryEmail(foundPayment.email, foundPayment.licenseKey, foundPayment.timestamp);
    } catch (emailErr) {
      console.error('[recoverKey:fallback] Email send failed:', emailErr.message);
      return res.status(500).json({ error: 'Failed to send recovery email. Please try again or contact support.' });
    }

    return res.status(200).json({
      success: true,
      message: 'Your license key has been sent to your email address. Please check your inbox (and spam folder).'
    });

  } catch (err) {
    console.error('[recoverKey:fallback] Error:', err);
    return res.status(500).json({ error: 'Recovery failed. Please contact support.' });
  }
}
