// GET /api/region → { country, currency }
// The pricing page asks which currency this visitor will be charged in, so the price it
// shows always matches what createOrder / createSubscription will charge.
const { setSecurityHeaders } = require('./_firebaseAdmin');
const { countryOf, currencyForRequest } = require('./_billing');

module.exports = (req, res) => {
  setSecurityHeaders(res);
  if (req.method !== 'GET') return res.status(405).json({ error: 'Method Not Allowed' });
  const country = countryOf(req);
  res.status(200).json({ country, currency: country ? currencyForRequest(req) : '', detected: Boolean(country) });
};
