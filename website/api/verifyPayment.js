const crypto = require('crypto');

// Same HMAC-SHA256 algorithm as the desktop app for license-key checksum
const HMAC_SECRET = process.env.HMAC_SECRET || "FS_Pro_Kx9m4R7vQ2nE8wLp_2026";

function generateProKey() {
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
  res.setHeader('Access-Control-Allow-Credentials', true);
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'GET,OPTIONS,PATCH,DELETE,POST,PUT');
  res.setHeader(
    'Access-Control-Allow-Headers',
    'X-CSRF-Token, X-Requested-With, Accept, Accept-Version, Content-Length, Content-MD5, Content-Type, Date, X-Api-Version'
  );

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

    // Verify HMAC-SHA256 signature
    const expectedSignature = crypto
      .createHmac("sha256", key_secret)
      .update(`${razorpay_order_id}|${razorpay_payment_id}`)
      .digest("hex");

    if (expectedSignature !== razorpay_signature) {
      return res.status(400).json({ error: 'Payment signature mismatch.' });
    }

    // Generate license key
    const licenseKey = generateProKey();

    // Log payment to Firebase RTDB (REST PUT - non-blocking & lightweight)
    const dbUrl = process.env.FIREBASE_RTDB_URL || 'https://flyshelf-official-pay-default-rtdb.firebaseio.com';
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
      
      // Fire and forget PUT to Firebase RTDB REST endpoint
      fetch(`${dbUrl}/payments/${razorpay_payment_id}.json`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(record)
      }).catch(dbErr => console.warn('Database write failed (non-blocking):', dbErr));
      
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
