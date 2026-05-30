const Razorpay = require('razorpay');

module.exports = async (req, res) => {
  // Handle CORS
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

    const { email, deviceId } = req.body;
    if (!email || !deviceId) {
      return res.status(400).json({ error: 'Missing email or deviceId' });
    }

    const key_id = process.env.RAZORPAY_KEY_ID;
    const key_secret = process.env.RAZORPAY_KEY_SECRET;

    if (!key_id || !key_secret) {
      return res.status(500).json({ error: 'Razorpay keys not configured on server.' });
    }

    const razorpay = new Razorpay({ key_id, key_secret });

    const receiptId = `rcpt_${Date.now()}_${Math.random().toString(36).substring(2, 6)}`;
    
    const order = await razorpay.orders.create({
      amount: 29900, // ₹299 in paise
      currency: 'INR',
      receipt: receiptId,
      notes: {
        email,
        deviceId,
        product: 'FlyShelf Pro Lifetime'
      }
    });

    return res.status(200).json({
      orderId: order.id,
      amount: order.amount,
      currency: order.currency,
      keyId: key_id
    });

  } catch (err) {
    console.error('Vercel createOrder Error:', err);
    return res.status(500).json({ error: err.message || 'Failed to initiate order.' });
  }
};
