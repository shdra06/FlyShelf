// ═══════════════════════════════════════════════════════════════════
// Key Recovery API — Lets users retrieve their license key by email
//
// If a user paid but lost their key (power cut, browser crash, etc.),
// they can enter their email to get it back.
//
// POST /api/recoverKey  { email: "user@example.com" }
// Returns: { success: true, licenseKey: "FS-PRO-..." } or error
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
    res.setHeader('Access-Control-Allow-Origin', ALLOWED_ORIGINS[0]);
  }
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

    if (!email || !email.includes('@')) {
      return res.status(400).json({ error: 'Please provide a valid email address.' });
    }

    const normalizedEmail = email.trim().toLowerCase();

    const dbUrl = process.env.FIREBASE_RTDB_URL || 'https://flyshelf-official-pay-default-rtdb.firebaseio.com';

    // ─── Search all payments for this email ───
    // Firebase RTDB doesn't support native queries without indexing,
    // so we use orderBy + equalTo on the email field.
    // This requires a Firebase rule: ".indexOn": ["email"] under /payments
    const searchUrl = `${dbUrl}/payments.json?orderBy="email"&equalTo="${normalizedEmail}"&limitToLast=5`;

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
      return res.status(404).json({
        error: 'No completed payment found for this email. If you just paid, please wait 2 minutes and try again.'
      });
    }

    return res.status(200).json({
      success: true,
      licenseKey: latestPayment.licenseKey,
      email: latestPayment.email,
      purchasedAt: latestPayment.timestamp
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

    return res.status(200).json({
      success: true,
      licenseKey: foundPayment.licenseKey,
      email: foundPayment.email,
      purchasedAt: foundPayment.timestamp
    });

  } catch (err) {
    console.error('[recoverKey:fallback] Error:', err);
    return res.status(500).json({ error: 'Recovery failed. Please contact support.' });
  }
}
