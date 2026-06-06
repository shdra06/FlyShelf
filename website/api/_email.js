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

// ─── Inline SVG Icons (email-safe, no external dependencies) ───

const ICON = {
  // Airplane / FlyShelf brand mark
  plane: `<img src="data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSIyNCIgaGVpZ2h0PSIyNCIgdmlld0JveD0iMCAwIDI0IDI0IiBmaWxsPSJub25lIiBzdHJva2U9IiMwMDAiIHN0cm9rZS13aWR0aD0iMiIgc3Ryb2tlLWxpbmVjYXA9InJvdW5kIiBzdHJva2UtbGluZWpvaW49InJvdW5kIj48cGF0aCBkPSJNMTcuOCAyMC40IDIxIDNsLTE3LjMgN2g0LjVMOSAxN2wYLjgtNi42eiIvPjwvc3ZnPg==" width="22" height="22" alt="" style="vertical-align:middle;" />`,

  // Checkmark circle (success)
  check: `<img src="data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSIyMCIgaGVpZ2h0PSIyMCIgdmlld0JveD0iMCAwIDI0IDI0IiBmaWxsPSJub25lIiBzdHJva2U9IiMxMGI5ODEiIHN0cm9rZS13aWR0aD0iMiI+PGNpcmNsZSBjeD0iMTIiIGN5PSIxMiIgcj0iMTAiLz48cGF0aCBkPSJtOSAxMiAyIDIgNC00Ii8+PC9zdmc+" width="18" height="18" alt="" style="vertical-align:middle;" />`,

  // Key icon
  key: `<img src="data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSIyMCIgaGVpZ2h0PSIyMCIgdmlld0JveD0iMCAwIDI0IDI0IiBmaWxsPSJub25lIiBzdHJva2U9IiMwMGQyZmYiIHN0cm9rZS13aWR0aD0iMiI+PHBhdGggZD0ibTIxIDIgLTIgMm0tNy42MSA3LjYxYTUuNSA1LjUgMCAxIDAtNy43NCA3Ljc0IDUuNSA1LjUgMCAwIDAgNy43NC03Ljc0em0wIDBMMTUuNSA3LjVtMCAwIDMgM0wxNCAxNGwzIDNoLTNsMy0zIi8+PC9zdmc+" width="18" height="18" alt="" style="vertical-align:middle;" />`,

  // Lightning bolt (activate)
  bolt: `<img src="data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSIxNiIgaGVpZ2h0PSIxNiIgdmlld0JveD0iMCAwIDI0IDI0IiBmaWxsPSIjMDAwIiBzdHJva2U9Im5vbmUiPjxwYXRoIGQ9Ik0xMyAyTDMgMTRoOWwtMS0xMmgybC0xIDEySDE0bDktMTJoLTlsMSAxMnoiLz48L3N2Zz4=" width="14" height="14" alt="" style="vertical-align:middle;" />`,

  // Info / tip icon
  info: `<img src="data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSIxNiIgaGVpZ2h0PSIxNiIgdmlld0JveD0iMCAwIDI0IDI0IiBmaWxsPSJub25lIiBzdHJva2U9IiM2NjYiIHN0cm9rZS13aWR0aD0iMiI+PGNpcmNsZSBjeD0iMTIiIGN5PSIxMiIgcj0iMTAiLz48cGF0aCBkPSJNMTIgMTZ2LTRtMC00aC4wMSIvPjwvc3ZnPg==" width="14" height="14" alt="" style="vertical-align:middle;" />`,

  // Shield icon (security)
  shield: `<img src="data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSIxNCIgaGVpZ2h0PSIxNCIgdmlld0JveD0iMCAwIDI0IDI0IiBmaWxsPSJub25lIiBzdHJva2U9IiM2NjYiIHN0cm9rZS13aWR0aD0iMiI+PHBhdGggZD0iTTEyIDIycy04LTQtOC0xMFY1bDgtM2w4IDN2N2MwIDYtOCAxMC04IDEweiIvPjwvc3ZnPg==" width="12" height="12" alt="" style="vertical-align:middle;" />`,
};

// ─── Branded HTML email shell (premium dark theme) ───
function emailShell(title, bodyContent) {
  return `<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>${title}</title>
  <!--[if mso]>
  <style>body,table,td{font-family:Segoe UI,sans-serif !important;}</style>
  <![endif]-->
</head>
<body style="margin:0;padding:0;background:#08090d;font-family:'Segoe UI','Helvetica Neue',Helvetica,Arial,sans-serif;-webkit-font-smoothing:antialiased;">
  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#08090d;padding:40px 16px;">
    <tr><td align="center">
      <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:540px;background:#111218;border:1px solid #1e1f2a;border-radius:12px;overflow:hidden;">
        
        <!-- Header Bar -->
        <tr><td style="background:linear-gradient(135deg,#00c6fb 0%,#005bea 100%);padding:24px 32px;">
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0">
            <tr>
              <td style="text-align:left;">
                <h1 style="margin:0;font-size:20px;font-weight:700;color:#fff;letter-spacing:-0.3px;">FlyShelf Pro</h1>
              </td>
              <td style="text-align:right;">
                <span style="display:inline-block;background:rgba(255,255,255,0.2);border-radius:6px;padding:4px 10px;font-size:11px;font-weight:600;color:#fff;letter-spacing:0.5px;text-transform:uppercase;">Lifetime License</span>
              </td>
            </tr>
          </table>
        </td></tr>
        
        <!-- Body -->
        <tr><td style="padding:32px 32px 28px;">
          ${bodyContent}
        </td></tr>
        
        <!-- Divider -->
        <tr><td style="padding:0 32px;">
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0">
            <tr><td style="border-top:1px solid #1e1f2a;font-size:0;line-height:0;">&nbsp;</td></tr>
          </table>
        </td></tr>
        
        <!-- Footer -->
        <tr><td style="padding:20px 32px 24px;text-align:center;">
          <p style="margin:0 0 6px;font-size:11px;color:#555;line-height:1.6;">
            ${ICON.shield} This is a secure automated message from FlyShelf. Do not share your license key.
          </p>
          <p style="margin:0;font-size:11px;color:#444;">
            <a href="https://fly-shelf.vercel.app" style="color:#00a8e8;text-decoration:none;font-weight:600;">fly-shelf.vercel.app</a>
          </p>
        </td></tr>
        
      </table>

      <!-- Sub-footer -->
      <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:540px;">
        <tr><td style="padding:16px 0;text-align:center;">
          <p style="margin:0;font-size:10px;color:#333;line-height:1.5;">
            You received this email because a purchase was made with this address.
          </p>
        </td></tr>
      </table>
    </td></tr>
  </table>
</body>
</html>`;
}

// ─── License key display block ───
function keyBlock(licenseKey) {
  return `
    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin:24px 0 20px;">
      <tr><td style="background:#0c0d14;border:1px solid #1e1f2a;border-radius:10px;padding:20px 24px;text-align:center;">
        <p style="margin:0 0 10px;font-size:10px;color:#666;text-transform:uppercase;letter-spacing:1.5px;font-weight:700;">
          ${ICON.key}&nbsp; License Key
        </p>
        <p style="margin:0;font-size:22px;font-weight:700;color:#00c6fb;letter-spacing:3px;font-family:'Cascadia Code','SF Mono','Courier New',monospace;line-height:1.4;">
          ${licenseKey}
        </p>
      </td></tr>
    </table>`;
}

// ─── Activate button ───
function activateButton(licenseKey) {
  return `
    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin:8px 0 24px;">
      <tr><td align="center">
        <!--[if mso]>
        <v:roundrect xmlns:v="urn:schemas-microsoft-com:vml" href="flyshelf://activate?key=${encodeURIComponent(licenseKey)}" style="height:46px;v-text-anchor:middle;width:220px;" arcsize="50%" fill="t">
          <v:fill type="gradient" color="#00c6fb" color2="#005bea" angle="135"/>
          <v:textbox inset="0,0,0,0"><center style="font-family:sans-serif;font-size:14px;font-weight:700;color:#fff;">Activate Now</center></v:textbox>
        </v:roundrect>
        <![endif]-->
        <!--[if !mso]><!-->
        <a href="flyshelf://activate?key=${encodeURIComponent(licenseKey)}" 
           style="display:inline-block;background:linear-gradient(135deg,#00c6fb,#005bea);color:#fff;text-decoration:none;padding:13px 36px;border-radius:24px;font-weight:700;font-size:14px;letter-spacing:0.3px;line-height:1;">
          ${ICON.bolt}&nbsp; Activate Now
        </a>
        <!--<![endif]-->
      </td></tr>
    </table>
    <p style="margin:0;font-size:11px;color:#444;text-align:center;line-height:1.6;">
      Click while FlyShelf is running on your PC, or paste the key in <strong style="color:#888;">Settings &rarr; Activate Pro</strong>.
    </p>`;
}

// ─── Order details row ───
function detailRow(label, value) {
  return `
    <tr>
      <td style="padding:6px 0;font-size:12px;color:#666;font-weight:600;">${label}</td>
      <td style="padding:6px 0;font-size:12px;color:#bbb;text-align:right;">${value}</td>
    </tr>`;
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
  const now = new Date();
  const dateStr = now.toLocaleDateString('en-US', { year: 'numeric', month: 'long', day: 'numeric' });

  const body = `
    <!-- Status Badge -->
    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin-bottom:20px;">
      <tr><td>
        <span style="display:inline-block;background:rgba(16,185,129,0.12);border:1px solid rgba(16,185,129,0.25);border-radius:20px;padding:5px 14px;font-size:12px;font-weight:600;color:#10b981;letter-spacing:0.3px;">
          ${ICON.check}&nbsp; Payment Confirmed
        </span>
      </td></tr>
    </table>

    <h2 style="margin:0 0 8px;font-size:20px;font-weight:700;color:#f0f0f5;letter-spacing:-0.3px;">Your license is ready</h2>
    <p style="margin:0 0 4px;font-size:14px;color:#8b8d9a;line-height:1.6;">
      Thank you for purchasing FlyShelf Pro. Your lifetime license has been generated and is ready to activate.
    </p>

    ${keyBlock(licenseKey)}
    ${activateButton(licenseKey)}

    <!-- Order Details -->
    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin-top:28px;background:#0c0d14;border:1px solid #1e1f2a;border-radius:10px;padding:16px 20px;">
      <tr><td>
        <p style="margin:0 0 10px;font-size:10px;color:#555;text-transform:uppercase;letter-spacing:1.2px;font-weight:700;">Order Details</p>
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
      <tr><td style="background:rgba(0,198,251,0.05);border:1px solid rgba(0,198,251,0.1);border-radius:8px;padding:14px 16px;">
        <p style="margin:0;font-size:12px;color:#666;line-height:1.6;">
          ${ICON.info}&nbsp; <strong style="color:#888;">Pro tip:</strong> Copy the key above while FlyShelf is running &mdash; it auto-detects Pro keys from your clipboard and activates instantly.
        </p>
      </td></tr>
    </table>
  `;

  try {
    const info = await t.sendMail({
      from: `FlyShelf <${senderEmail}>`,
      to: email,
      subject: 'Your FlyShelf Pro License Key',
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
        <span style="display:inline-block;background:rgba(0,198,251,0.1);border:1px solid rgba(0,198,251,0.2);border-radius:20px;padding:5px 14px;font-size:12px;font-weight:600;color:#00c6fb;letter-spacing:0.3px;">
          ${ICON.key}&nbsp; Key Recovered
        </span>
      </td></tr>
    </table>

    <h2 style="margin:0 0 8px;font-size:20px;font-weight:700;color:#f0f0f5;letter-spacing:-0.3px;">License key recovered</h2>
    <p style="margin:0 0 4px;font-size:14px;color:#8b8d9a;line-height:1.6;">
      We found your FlyShelf Pro license associated with this email. Here is your key:
    </p>

    ${keyBlock(licenseKey)}
    ${activateButton(licenseKey)}

    <!-- Recovery Details -->
    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin-top:28px;background:#0c0d14;border:1px solid #1e1f2a;border-radius:10px;padding:16px 20px;">
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
      from: `FlyShelf <${senderEmail}>`,
      to: email,
      subject: 'Your FlyShelf Pro License Key (Recovery)',
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
