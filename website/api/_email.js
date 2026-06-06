// ═══════════════════════════════════════════════════════════════════
// FlyShelf Email Module — Shared transactional email utilities
//
// Uses Nodemailer + Gmail OAuth2 for email delivery.
// Set these env vars in Vercel:
//   GMAIL_USER           — your Gmail address (e.g. flyshelfhelp@gmail.com)
//   GMAIL_CLIENT_ID      — OAuth2 Client ID from Google Cloud Console
//   GMAIL_CLIENT_SECRET  — OAuth2 Client Secret
//   GMAIL_REFRESH_TOKEN  — OAuth2 Refresh Token (obtained via OAuth Playground)
//
// This file starts with _ so Vercel does NOT expose it as an API route.
// ═══════════════════════════════════════════════════════════════════

const nodemailer = require('nodemailer');

let transporter = null;

function getTransporter() {
  if (transporter) return transporter;

  const user = process.env.GMAIL_USER;
  const clientId = process.env.GMAIL_CLIENT_ID;
  const clientSecret = process.env.GMAIL_CLIENT_SECRET;
  const refreshToken = process.env.GMAIL_REFRESH_TOKEN;

  if (!user || !clientId || !clientSecret || !refreshToken) {
    console.warn('[email] Gmail OAuth2 credentials not fully configured — emails will be skipped');
    return null;
  }

  transporter = nodemailer.createTransport({
    service: 'gmail',
    auth: {
      type: 'OAuth2',
      user,
      clientId,
      clientSecret,
      refreshToken,
    },
  });

  return transporter;
}

// ─── Branded HTML email shell ───
function emailShell(title, bodyContent) {
  return `<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>${title}</title>
</head>
<body style="margin:0;padding:0;background:#0a0b10;font-family:'Segoe UI',system-ui,-apple-system,sans-serif;">
  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#0a0b10;padding:40px 20px;">
    <tr><td align="center">
      <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:520px;background:#12131a;border:1px solid rgba(255,255,255,0.06);border-radius:16px;overflow:hidden;">
        
        <!-- Header -->
        <tr><td style="background:linear-gradient(135deg,#00d2ff 0%,#0b72e7 100%);padding:28px 30px;text-align:center;">
          <h1 style="margin:0;font-size:22px;font-weight:800;color:#000;letter-spacing:-0.5px;">✈️ FlyShelf Pro</h1>
        </td></tr>
        
        <!-- Body -->
        <tr><td style="padding:30px;">
          ${bodyContent}
        </td></tr>
        
        <!-- Footer -->
        <tr><td style="padding:20px 30px;border-top:1px solid rgba(255,255,255,0.06);text-align:center;">
          <p style="margin:0;font-size:11px;color:#666;line-height:1.5;">
            This is an automated email from FlyShelf. Do not share your license key with anyone.<br>
            <a href="https://fly-shelf.vercel.app" style="color:#00d2ff;text-decoration:none;">fly-shelf.vercel.app</a>
          </p>
        </td></tr>
        
      </table>
    </td></tr>
  </table>
</body>
</html>`;
}

// ─── License key block (reused in both email types) ───
function keyBlock(licenseKey) {
  return `
    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin:20px 0;">
      <tr><td style="background:#1a1b25;border:2px dashed rgba(0,210,255,0.35);border-radius:12px;padding:18px;text-align:center;">
        <p style="margin:0;font-size:12px;color:#888;text-transform:uppercase;letter-spacing:1px;font-weight:700;">Your License Key</p>
        <p style="margin:10px 0 0;font-size:20px;font-weight:800;color:#00d2ff;letter-spacing:2px;font-family:'Courier New',monospace;">${licenseKey}</p>
      </td></tr>
    </table>`;
}

// ─── Deep link activate button ───
function activateButton(licenseKey) {
  return `
    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin:10px 0 20px;">
      <tr><td align="center">
        <a href="flyshelf://activate?key=${encodeURIComponent(licenseKey)}" 
           style="display:inline-block;background:linear-gradient(135deg,#00d2ff,#0b72e7);color:#000;text-decoration:none;padding:14px 32px;border-radius:25px;font-weight:700;font-size:14px;">
          ⚡ One-Click Activate
        </a>
      </td></tr>
    </table>
    <p style="margin:0;font-size:11px;color:#555;text-align:center;line-height:1.5;">
      Click the button above while FlyShelf is running on your PC, or paste the key manually in Settings → Activate Pro.
    </p>`;
}

// ═══════════════════════════════════════════════════════════════════
// PUBLIC API
// ═══════════════════════════════════════════════════════════════════

/**
 * Send purchase confirmation email with the license key.
 * Non-blocking — call with .catch() to not disrupt the main flow.
 */
async function sendPurchaseEmail(email, licenseKey, paymentId) {
  const t = getTransporter();
  if (!t) return { skipped: true };

  const senderEmail = process.env.GMAIL_USER;

  const body = `
    <h2 style="margin:0 0 8px;font-size:20px;font-weight:800;color:#fff;">Payment Successful! 🎉</h2>
    <p style="margin:0 0 5px;font-size:14px;color:#aaa;line-height:1.6;">
      Thank you for purchasing FlyShelf Pro. Your license is ready to activate.
    </p>
    <p style="margin:0;font-size:12px;color:#666;">
      Payment ID: ${paymentId ? paymentId.substring(0, 12) + '...' : 'N/A'}
    </p>

    ${keyBlock(licenseKey)}
    ${activateButton(licenseKey)}

    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin-top:20px;background:rgba(0,210,255,0.06);border-radius:8px;padding:14px;">
      <tr><td>
        <p style="margin:0;font-size:12px;color:#888;line-height:1.5;">
          💡 <strong style="color:#ccc;">Tip:</strong> Copy the key above (Ctrl+C) while FlyShelf is running — it auto-detects Pro keys on your clipboard and activates instantly!
        </p>
      </td></tr>
    </table>
  `;

  try {
    const info = await t.sendMail({
      from: `FlyShelf Pro <${senderEmail}>`,
      to: email,
      subject: '🔑 Your FlyShelf Pro License Key',
      html: emailShell('FlyShelf Pro — License Key', body),
    });
    console.log(`[email] Purchase confirmation sent to ${email.substring(0, 3)}*** (id: ${info.messageId})`);
    return { messageId: info.messageId };
  } catch (err) {
    console.error('[email] Failed to send purchase email:', err.message);
    return { error: err.message };
  }
}

/**
 * Send key recovery email.
 * This is the ONLY way users get their key back — it is NOT returned in the API response.
 */
async function sendRecoveryEmail(email, licenseKey, purchasedAt) {
  const t = getTransporter();
  if (!t) return { skipped: true };

  const senderEmail = process.env.GMAIL_USER;

  const purchaseDate = purchasedAt
    ? new Date(purchasedAt).toLocaleDateString('en-US', { year: 'numeric', month: 'long', day: 'numeric' })
    : 'N/A';

  const body = `
    <h2 style="margin:0 0 8px;font-size:20px;font-weight:800;color:#fff;">License Key Recovery 🔑</h2>
    <p style="margin:0 0 5px;font-size:14px;color:#aaa;line-height:1.6;">
      We found your FlyShelf Pro license. Here's your key:
    </p>
    <p style="margin:0;font-size:12px;color:#666;">
      Original purchase: ${purchaseDate}
    </p>

    ${keyBlock(licenseKey)}
    ${activateButton(licenseKey)}
  `;

  try {
    const info = await t.sendMail({
      from: `FlyShelf Pro <${senderEmail}>`,
      to: email,
      subject: '🔑 Your FlyShelf Pro License Key (Recovery)',
      html: emailShell('FlyShelf Pro — Key Recovery', body),
    });
    console.log(`[email] Recovery email sent to ${email.substring(0, 3)}*** (id: ${info.messageId})`);
    return { messageId: info.messageId };
  } catch (err) {
    console.error('[email] Failed to send recovery email:', err.message);
    return { error: err.message };
  }
}

module.exports = { sendPurchaseEmail, sendRecoveryEmail };
