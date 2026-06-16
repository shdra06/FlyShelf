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

// ─── Clean HTML email shell (anti-spam optimized) ───
// NO inline data:image SVGs (major spam trigger)
// NO dark backgrounds (suspicious to filters)
// Clean, professional, light design that passes spam filters
function emailShell(title, bodyContent) {
  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <meta name="color-scheme" content="light">
  <meta name="supported-color-schemes" content="light">
  <title>${title}</title>
  <!--[if mso]>
  <style>body,table,td{font-family:Segoe UI,sans-serif !important;}</style>
  <![endif]-->
</head>
<body style="margin:0;padding:0;background-color:#f4f5f7;font-family:'Segoe UI','Helvetica Neue',Helvetica,Arial,sans-serif;-webkit-font-smoothing:antialiased;">
  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background-color:#f4f5f7;padding:40px 16px;">
    <tr><td align="center">
      <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:560px;background-color:#ffffff;border-radius:12px;overflow:hidden;box-shadow:0 1px 3px rgba(0,0,0,0.08);">
        
        <!-- Header -->
        <tr><td style="background-color:#0066ff;padding:28px 32px;text-align:center;">
          <h1 style="margin:0;font-size:22px;font-weight:700;color:#ffffff;letter-spacing:-0.3px;">FlyShelf Pro</h1>
          <p style="margin:6px 0 0;font-size:12px;color:rgba(255,255,255,0.8);font-weight:500;letter-spacing:0.5px;text-transform:uppercase;">Lifetime License</p>
        </td></tr>
        
        <!-- Body -->
        <tr><td style="padding:32px 32px 28px;">
          ${bodyContent}
        </td></tr>
        
        <!-- Footer -->
        <tr><td style="padding:0 32px;"><hr style="border:none;border-top:1px solid #eee;margin:0;"></td></tr>
        <tr><td style="padding:20px 32px 24px;text-align:center;">
          <p style="margin:0 0 6px;font-size:11px;color:#999;line-height:1.6;">
            This is an automated transactional email from FlyShelf. Do not share your license key.
          </p>
          <p style="margin:0;font-size:11px;color:#999;">
            <a href="https://fly-shelf.vercel.app" style="color:#0066ff;text-decoration:none;font-weight:600;">fly-shelf.vercel.app</a>
          </p>
        </td></tr>
        
      </table>

      <!-- Sub-footer -->
      <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:560px;">
        <tr><td style="padding:16px 0;text-align:center;">
          <p style="margin:0;font-size:10px;color:#aaa;line-height:1.5;">
            You received this email because a purchase was made with this address.
          </p>
        </td></tr>
      </table>
    </td></tr>
  </table>
</body>
</html>`;
}

// ─── License key display block (clean, no SVGs) ───
function keyBlock(licenseKey) {
  return `
    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin:24px 0 20px;">
      <tr><td style="background-color:#f8f9fb;border:2px dashed #d0d5dd;border-radius:10px;padding:22px 24px;text-align:center;">
        <p style="margin:0 0 10px;font-size:10px;color:#888;text-transform:uppercase;letter-spacing:1.5px;font-weight:700;">
          Your License Key
        </p>
        <p style="margin:0;font-size:22px;font-weight:700;color:#0066ff;letter-spacing:3px;font-family:'Cascadia Code','SF Mono','Courier New',monospace;line-height:1.4;">
          ${licenseKey}
        </p>
      </td></tr>
    </table>`;
}

// ─── Activate button (clean, no SVGs) ───
function activateButton(licenseKey) {
  return `
    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin:8px 0 24px;">
      <tr><td align="center">
        <!--[if mso]>
        <v:roundrect xmlns:v="urn:schemas-microsoft-com:vml" href="flyshelf://activate?key=${encodeURIComponent(licenseKey)}" style="height:46px;v-text-anchor:middle;width:220px;" arcsize="50%" fillcolor="#0066ff">
          <v:textbox inset="0,0,0,0"><center style="font-family:sans-serif;font-size:14px;font-weight:700;color:#fff;">Activate in FlyShelf</center></v:textbox>
        </v:roundrect>
        <![endif]-->
        <!--[if !mso]><!-->
        <a href="flyshelf://activate?key=${encodeURIComponent(licenseKey)}" 
           style="display:inline-block;background-color:#0066ff;color:#ffffff;text-decoration:none;padding:13px 36px;border-radius:24px;font-weight:700;font-size:14px;letter-spacing:0.3px;line-height:1;">
          Activate in FlyShelf
        </a>
        <!--<![endif]-->
      </td></tr>
    </table>
    <p style="margin:0;font-size:12px;color:#888;text-align:center;line-height:1.6;">
      Click while FlyShelf is running, or paste the key in <strong style="color:#555;">Settings &rarr; Activate Pro</strong>.
    </p>`;
}

// ─── Order details row ───
function detailRow(label, value) {
  return `
    <tr>
      <td style="padding:6px 0;font-size:12px;color:#888;font-weight:600;">${label}</td>
      <td style="padding:6px 0;font-size:12px;color:#333;text-align:right;">${value}</td>
    </tr>`;
}

// ─── Plain-text version generator (for multipart/alternative — anti-spam) ───
function purchasePlainText(licenseKey, paymentId, email, dateStr) {
  return `FlyShelf Pro — License Key

Thank you for purchasing FlyShelf Pro! Your lifetime license has been generated.

YOUR LICENSE KEY:
${licenseKey}

HOW TO ACTIVATE:
1. Open FlyShelf on your PC
2. Go to Settings → Activate Pro
3. Paste the key above

ORDER DETAILS:
- License Type: Pro — Lifetime
- Payment ID: ${paymentId || 'N/A'}
- Date: ${dateStr}
- Email: ${email}

Pro tip: Copy the key while FlyShelf is running — it auto-detects Pro keys from your clipboard.

---
This is an automated transactional email from FlyShelf.
Do not share your license key with anyone.
https://fly-shelf.vercel.app`;
}

function recoveryPlainText(licenseKey, purchaseDate) {
  return `FlyShelf Pro — License Key Recovery

We found your FlyShelf Pro license. Here is your key:

YOUR LICENSE KEY:
${licenseKey}

HOW TO ACTIVATE:
1. Open FlyShelf on your PC
2. Go to Settings → Activate Pro
3. Paste the key above

Original Purchase Date: ${purchaseDate}
License Type: Pro — Lifetime

---
This is an automated transactional email from FlyShelf.
https://fly-shelf.vercel.app`;
}

// ═══════════════════════════════════════════════════════════════════
// PUBLIC API
// ═══════════════════════════════════════════════════════════════════

/**
 * Send purchase confirmation email with the license key.
 * Includes plain-text alternative for spam filter compliance.
 */
async function sendPurchaseEmail(email, licenseKey, paymentId) {
  const t = getTransporter();
  if (!t) return { skipped: true };

  const senderEmail = process.env.GMAIL_USER;
  const now = new Date();
  const dateStr = now.toLocaleDateString('en-US', { year: 'numeric', month: 'long', day: 'numeric' });

  const body = `
    <!-- Status Badge -->
    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin-bottom:20px;">
      <tr><td>
        <span style="display:inline-block;background-color:#ecfdf5;border:1px solid #a7f3d0;border-radius:20px;padding:5px 14px;font-size:12px;font-weight:600;color:#059669;letter-spacing:0.3px;">
          &#10003;&nbsp; Payment Confirmed
        </span>
      </td></tr>
    </table>

    <h2 style="margin:0 0 8px;font-size:20px;font-weight:700;color:#111;letter-spacing:-0.3px;">Your license is ready</h2>
    <p style="margin:0 0 4px;font-size:14px;color:#666;line-height:1.6;">
      Thank you for purchasing FlyShelf Pro. Your lifetime license has been generated and is ready to activate.
    </p>

    ${keyBlock(licenseKey)}
    ${activateButton(licenseKey)}

    <!-- Order Details -->
    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin-top:28px;background-color:#f8f9fb;border:1px solid #eee;border-radius:10px;padding:16px 20px;">
      <tr><td>
        <p style="margin:0 0 10px;font-size:10px;color:#888;text-transform:uppercase;letter-spacing:1.2px;font-weight:700;">Order Details</p>
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0">
          ${detailRow('License Type', 'Pro &mdash; Lifetime')}
          ${detailRow('Payment ID', paymentId ? paymentId.substring(0, 14) + '...' : 'N/A')}
          ${detailRow('Date', dateStr)}
          ${detailRow('Email', email)}
        </table>
      </td></tr>
    </table>

    <!-- Pro Tip -->
    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin-top:20px;">
      <tr><td style="background-color:#eff6ff;border:1px solid #dbeafe;border-radius:8px;padding:14px 16px;">
        <p style="margin:0;font-size:12px;color:#666;line-height:1.6;">
          <strong style="color:#444;">Pro tip:</strong> Copy the key above while FlyShelf is running &mdash; it auto-detects Pro keys from your clipboard and activates instantly.
        </p>
      </td></tr>
    </table>
  `;

  try {
    const info = await t.sendMail({
      from: `"FlyShelf" <${senderEmail}>`,
      replyTo: senderEmail,
      to: email,
      subject: 'Your FlyShelf Pro License Key',
      // Plain-text alternative (critical for anti-spam)
      text: purchasePlainText(licenseKey, paymentId, email, dateStr),
      html: emailShell('FlyShelf Pro — License Key', body),
      headers: {
        'X-Mailer': 'FlyShelf License Server',
        'X-Entity-Ref-ID': paymentId || `fs-${Date.now()}`,
      },
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
 */
async function sendRecoveryEmail(email, licenseKey, purchasedAt) {
  const t = getTransporter();
  if (!t) return { skipped: true };

  const senderEmail = process.env.GMAIL_USER;

  const purchaseDate = purchasedAt
    ? new Date(purchasedAt).toLocaleDateString('en-US', { year: 'numeric', month: 'long', day: 'numeric' })
    : 'N/A';

  const body = `
    <!-- Status Badge -->
    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin-bottom:20px;">
      <tr><td>
        <span style="display:inline-block;background-color:#eff6ff;border:1px solid #bfdbfe;border-radius:20px;padding:5px 14px;font-size:12px;font-weight:600;color:#2563eb;letter-spacing:0.3px;">
          &#128273;&nbsp; Key Recovered
        </span>
      </td></tr>
    </table>

    <h2 style="margin:0 0 8px;font-size:20px;font-weight:700;color:#111;letter-spacing:-0.3px;">License key recovered</h2>
    <p style="margin:0 0 4px;font-size:14px;color:#666;line-height:1.6;">
      We found your FlyShelf Pro license associated with this email. Here is your key:
    </p>

    ${keyBlock(licenseKey)}
    ${activateButton(licenseKey)}

    <!-- Recovery Details -->
    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin-top:28px;background-color:#f8f9fb;border:1px solid #eee;border-radius:10px;padding:16px 20px;">
      <tr><td>
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0">
          ${detailRow('Original Purchase', purchaseDate)}
          ${detailRow('License Type', 'Pro &mdash; Lifetime')}
        </table>
      </td></tr>
    </table>
  `;

  try {
    const info = await t.sendMail({
      from: `"FlyShelf" <${senderEmail}>`,
      replyTo: senderEmail,
      to: email,
      subject: 'Your FlyShelf Pro License Key (Recovery)',
      text: recoveryPlainText(licenseKey, purchaseDate),
      html: emailShell('FlyShelf Pro — Key Recovery', body),
      headers: {
        'X-Mailer': 'FlyShelf License Server',
        'X-Entity-Ref-ID': `fs-recovery-${Date.now()}`,
      },
    });
    console.log(`[email] Recovery email sent to ${email.substring(0, 3)}*** (id: ${info.messageId})`);
    return { messageId: info.messageId };
  } catch (err) {
    console.error('[email] Failed to send recovery email:', err.message);
    return { error: err.message };
  }
}

module.exports = { sendPurchaseEmail, sendRecoveryEmail };
