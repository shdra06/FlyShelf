/**
 * FlyShelf Pro — Razorpay Payment Processing Cloud Functions
 *
 * Functions:
 *   createRazorpayOrder   – Creates a Razorpay order for FlyShelf Pro (₹299)
 *   verifyRazorpayPayment – Verifies payment signature, generates license key,
 *                           and stores the record in Firebase RTDB
 */

const functions = require("firebase-functions");
const admin = require("firebase-admin");
const crypto = require("crypto");
const Razorpay = require("razorpay");
const cors = require("cors")({ origin: true });

// ---------------------------------------------------------------------------
// Firebase Admin initialisation
// ---------------------------------------------------------------------------
admin.initializeApp();
const db = admin.database();

// ---------------------------------------------------------------------------
// Razorpay configuration (functions.config > env vars > hardcoded test keys)
// ---------------------------------------------------------------------------
function getRazorpayKeyId() {
  try {
    return functions.config().razorpay.key_id;
  } catch (_) {
    return process.env.RAZORPAY_KEY_ID || "rzp_test_SvCf5HlgqjXlLk";
  }
}

function getRazorpayKeySecret() {
  try {
    return functions.config().razorpay.key_secret;
  } catch (_) {
    return process.env.RAZORPAY_KEY_SECRET || "un8XK4YJ7ufxnkZeWTvN0zH6";
  }
}

const RAZORPAY_KEY_ID = getRazorpayKeyId();
const RAZORPAY_KEY_SECRET = getRazorpayKeySecret();

const razorpayInstance = new Razorpay({
  key_id: RAZORPAY_KEY_ID,
  key_secret: RAZORPAY_KEY_SECRET,
});

// ---------------------------------------------------------------------------
// HMAC secret used for license-key checksum (must match the desktop app)
// ---------------------------------------------------------------------------
const HMAC_SECRET = "FlyShelf_Pro_2026_Secure_Salt";

// ---------------------------------------------------------------------------
// Helper — generate a FlyShelf Pro license key
// Format: FS-PRO-XXXX-XXXX-XXXX-XXXX  (12 random chars + 4 HMAC checksum)
// ---------------------------------------------------------------------------
function generateProKey() {
  const chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

  // Generate 12 random alphanumeric chars
  let random12 = "";
  for (let i = 0; i < 12; i++) {
    random12 += chars[crypto.randomInt(chars.length)];
  }

  // Compute 4-char HMAC checksum (same algorithm as the desktop app)
  const hmac = crypto.createHmac("sha256", HMAC_SECRET);
  hmac.update(random12);
  const checksum = hmac.digest("hex").substring(0, 4).toUpperCase();

  // Combine and format
  const payload = random12 + checksum; // 16 chars total
  return `FS-PRO-${payload.substring(0, 4)}-${payload.substring(4, 8)}-${payload.substring(8, 12)}-${payload.substring(12, 16)}`;
}

// ===========================================================================
// Cloud Function: createRazorpayOrder
// POST  { email, deviceId }
// Returns { orderId, amount, currency, keyId }
// ===========================================================================
exports.createRazorpayOrder = functions.https.onRequest((req, res) => {
  cors(req, res, async () => {
    try {
      // Only allow POST
      if (req.method !== "POST") {
        return res.status(405).json({ error: "Method not allowed. Use POST." });
      }

      const { email, deviceId } = req.body;

      if (!email || !deviceId) {
        return res.status(400).json({ error: "Missing required fields: email, deviceId" });
      }

      // Generate a unique receipt id
      const receiptId = `rcpt_${Date.now()}_${crypto.randomBytes(4).toString("hex")}`;

      // Create Razorpay order
      const orderOptions = {
        amount: 29900, // ₹299 in paise
        currency: "INR",
        receipt: receiptId,
        notes: {
          email,
          deviceId,
          product: "FlyShelf Pro Lifetime",
        },
      };

      const order = await razorpayInstance.orders.create(orderOptions);
      console.log(`[createRazorpayOrder] Order created: ${order.id} for ${email}`);

      return res.status(200).json({
        orderId: order.id,
        amount: order.amount,
        currency: order.currency,
        keyId: RAZORPAY_KEY_ID,
      });
    } catch (error) {
      console.error("[createRazorpayOrder] Error:", error);
      return res.status(500).json({ error: "Failed to create order. Please try again." });
    }
  });
});

// ===========================================================================
// Cloud Function: verifyRazorpayPayment
// POST  { razorpay_payment_id, razorpay_order_id, razorpay_signature,
//          email, deviceId }
// Returns { success: true, licenseKey: 'FS-PRO-...' }
// ===========================================================================
exports.verifyRazorpayPayment = functions.https.onRequest((req, res) => {
  cors(req, res, async () => {
    try {
      // Only allow POST
      if (req.method !== "POST") {
        return res.status(405).json({ error: "Method not allowed. Use POST." });
      }

      const {
        razorpay_payment_id,
        razorpay_order_id,
        razorpay_signature,
        email,
        deviceId,
      } = req.body;

      if (!razorpay_payment_id || !razorpay_order_id || !razorpay_signature) {
        return res.status(400).json({ error: "Missing payment verification fields." });
      }

      if (!email || !deviceId) {
        return res.status(400).json({ error: "Missing required fields: email, deviceId" });
      }

      // ----- STEP 1: Verify HMAC-SHA256 signature -----
      const expectedSignature = crypto
        .createHmac("sha256", RAZORPAY_KEY_SECRET)
        .update(`${razorpay_order_id}|${razorpay_payment_id}`)
        .digest("hex");

      if (expectedSignature !== razorpay_signature) {
        console.warn(
          `[verifyRazorpayPayment] Signature mismatch for payment ${razorpay_payment_id}`
        );
        return res.status(400).json({ error: "Payment verification failed. Invalid signature." });
      }

      console.log(
        `[verifyRazorpayPayment] Signature verified for payment ${razorpay_payment_id}`
      );

      // ----- STEP 2: Generate license key -----
      const licenseKey = generateProKey();
      console.log(
        `[verifyRazorpayPayment] License key generated for ${email}: ${licenseKey}`
      );

      // ----- STEP 3: Store payment record in RTDB -----
      const paymentRecord = {
        gateway: "razorpay",
        orderId: razorpay_order_id,
        paymentId: razorpay_payment_id,
        amount: 29900,
        currency: "INR",
        email,
        deviceId,
        licenseKey,
        status: "completed",
        timestamp: new Date().toISOString(),
      };

      await db.ref(`/payments/${razorpay_payment_id}`).set(paymentRecord);
      console.log(
        `[verifyRazorpayPayment] Payment record stored at /payments/${razorpay_payment_id}`
      );

      // ----- STEP 4: Store license key in /licenses/keys/{safeKey} -----
      const safeKey = licenseKey.replace(/\./g, "_").replace(/\//g, "_");
      await db.ref(`/licenses/keys/${safeKey}`).set({
        email,
        paymentId: razorpay_payment_id,
        generatedAt: new Date().toISOString(),
      });
      console.log(
        `[verifyRazorpayPayment] License stored at /licenses/keys/${safeKey}`
      );

      return res.status(200).json({
        success: true,
        licenseKey,
      });
    } catch (error) {
      console.error("[verifyRazorpayPayment] Error:", error);
      return res.status(500).json({ error: "Payment verification failed. Please contact support." });
    }
  });
});
