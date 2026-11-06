// GET /api/region → { country, currency, detected, plans: { lifetime, pro_monthly, pro_yearly } }
// The pricing page asks which currency this visitor will be charged in, so the price it
// shows always matches what createOrder / createSubscription will charge.
const { setSecurityHeaders } = require('./_firebaseAdmin');
const { countryOf, currencyForRequest, availablePlans } = require('./_billing');

module.exports = (req, res) => {
  setSecurityHeaders(res);
  if (req.method !== 'GET') return res.status(405).json({ error: 'Method Not Allowed' });
  const country = countryOf(req);
  const currency = country ? currencyForRequest(req) : '';
  // plans: which ones can be bought in this currency right now (a subscription needs its Razorpay plan).
  res.status(200).json({ country, currency, detected: Boolean(country), plans: currency ? availablePlans(currency) : null });
};
