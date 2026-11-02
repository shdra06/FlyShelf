/* FlyShelf Pro checkout. Prices, payment verification and key generation stay on the server
   (api/createOrder + api/verifyPayment for Lifetime, api/createSubscription + api/verifySubscription
   for Monthly/Yearly, api/manageSubscription for the account box); this file only presents the flow. */
(() => {
  'use strict';
  const $ = selector => document.querySelector(selector);
  const motionOK = () => window.FlyShelfSite?.motionAllowed() ?? !matchMedia('(prefers-reduced-motion: reduce)').matches;

  const tabInr = $('#tab-inr');
  const tabUsd = $('#tab-usd');
  const priceUsd = $('#price-display-usd');
  const priceInr = $('#price-display-inr');
  const inrDetails = $('#razorpay-details');
  const usdDetails = $('#paypal-details');
  const form = $('#checkout-form');
  const payButton = $('#btn-submit-payment');
  const payLabel = payButton.querySelector('span');
  const email = $('#customer-email');
  const emailError = $('#email-error');
  const status = $('#checkout-status');
  const planButtons = [...document.querySelectorAll('.billing-option')];

  const modal = $('#checkout-modal');
  const dialog = modal.querySelector('.modal-content');
  const stages = { loading: $('#modal-stage-loading'), success: $('#modal-stage-success'), error: $('#modal-stage-error') };
  const keyOutput = $('#license-key-output');

  /* Display prices only; the server decides what is charged. Amounts in paise/cents. */
  const PLANS = {
    pro_monthly: {
      title: 'FlyShelf Pro Monthly', sub: 'Billed monthly · cancel any time', every: 'a month', period: 'month',
      INR: { amount: 9900, price: '₹99' }, USD: { amount: 299, price: '$2.99' },
    },
    pro_yearly: {
      title: 'FlyShelf Pro Yearly', sub: 'Billed yearly · cancel any time', every: 'a year', period: 'year',
      INR: { amount: 79900, price: '₹799', monthly: '₹67' }, USD: { amount: 1999, price: '$19.99', monthly: '$1.67' },
    },
    lifetime: {
      title: 'FlyShelf Pro Lifetime', sub: 'One-time payment · nothing to renew', every: '', period: '',
      INR: { amount: 199900, price: '₹1,999' }, USD: { amount: 5000, price: '$50' },
    },
  };
  const METHODS = { subscription: 'UPI Autopay or cards', lifetime: 'UPI, cards or netbanking' };

  let region = 'USD';
  let plan = 'pro_yearly';
  let generatedKey = '';
  let busy = false;
  let lastFocus = null;

  // The desktop app may pass its device id when it opens this page.
  const deviceId = new URLSearchParams(location.search).get('deviceId') || 'web_purchase';

  function say(element, message, tone = 'info') {
    element.dataset.tone = tone;
    element.textContent = message;
  }
  const isSubscription = name => name === 'pro_monthly' || name === 'pro_yearly';
  const priceLabel = (name, currency) => {
    const def = PLANS[name];
    return def.every ? `${def[currency].price} ${def.every}` : def[currency].price;
  };
  function yearlySavings(currency) {
    const yearly = PLANS.pro_yearly[currency].amount;
    const monthly = PLANS.pro_monthly[currency].amount * 12;
    return Math.round((1 - yearly / monthly) * 100);
  }
  function formatDate(iso) {
    const time = Date.parse(iso || '');
    return Number.isNaN(time) ? '' : new Date(time).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
  }

  /* ── Plan and region ─────────────────────────────────────────────────── */
  function render() {
    const def = PLANS[plan];
    const india = region === 'INR';
    const sub = isSubscription(plan);
    tabInr.classList.toggle('active', india);
    tabUsd.classList.toggle('active', !india);
    tabInr.setAttribute('aria-pressed', String(india));
    tabUsd.setAttribute('aria-pressed', String(!india));
    for (const button of planButtons) {
      const on = button.dataset.plan === plan;
      button.classList.toggle('active', on);
      button.setAttribute('aria-pressed', String(on));
    }
    $('#yearly-savings').textContent = `Save ${yearlySavings(region)}%`;
    $('#plan-title').textContent = def.title;
    $('#plan-sub').textContent = def.sub;
    priceInr.textContent = def.INR.price;
    priceUsd.textContent = def.USD.price;
    priceInr.hidden = !india;
    priceUsd.hidden = india;
    const cur = def[region];
    $('#price-note').textContent = plan === 'pro_yearly' ? `a year · about ${cur.monthly} a month` : def.every || 'once, yours to keep';
    $('#tab-inr-note').textContent = `${priceLabel(plan, 'INR')} · ${sub ? 'UPI Autopay, cards' : 'UPI, cards, netbanking'}`;
    $('#tab-usd-note').textContent = `${priceLabel(plan, 'USD')} · Visa, Mastercard, Amex`;
    $('#inr-methods').textContent = sub ? METHODS.subscription : METHODS.lifetime;
    inrDetails.hidden = !india;
    usdDetails.hidden = india;
    $('#renew-note').hidden = !sub;
    if (sub) $('#renew-text').textContent = `Renews at ${priceLabel(plan, region)} until you cancel. Cancel any time under Manage subscription on this page — Pro stays on until the paid ${def.period} ends.`;
    if (!busy) payLabel.textContent = `Pay ${priceLabel(plan, region)}`;
  }
  let regionLocked = false;
  function setRegion(next) { if (!regionLocked) { region = next; render(); } }
  function setPlan(next) { if (PLANS[next]) { plan = next; render(); } }
  tabInr.addEventListener('click', () => setRegion('INR'));
  tabUsd.addEventListener('click', () => setRegion('USD'));
  for (const button of planButtons) button.addEventListener('click', () => setPlan(button.dataset.plan));
  // Links like pricing.html?plan=lifetime (or from the desktop app) preselect a plan.
  const requestedPlan = new URLSearchParams(location.search).get('plan');
  if (requestedPlan === 'monthly' || requestedPlan === 'yearly') plan = `pro_${requestedPlan}`;
  else if (requestedPlan === 'lifetime') plan = 'lifetime';
  // First guess from the time zone, then the server's answer (by country) replaces it and locks the choice:
  // India sees ₹, everyone else sees $, and checkout charges exactly what is shown.
  try { if (/^Asia\/(Kolkata|Calcutta)$/.test(Intl.DateTimeFormat().resolvedOptions().timeZone)) region = 'INR'; } catch { /* Default stays international. */ }
  render();
  fetch('/api/region', { headers: { Accept: 'application/json' } })
    .then((r) => (r.ok ? r.json() : null))
    .then((info) => {
      if (!info || !info.detected || (info.currency !== 'INR' && info.currency !== 'USD')) return;
      region = info.currency;
      regionLocked = true;
      const india = region === 'INR';
      tabInr.hidden = !india;
      tabUsd.hidden = india;
      $('#region-legend').textContent = india ? 'Prices for India, in rupees' : 'Prices in US dollars';
      render();
    })
    .catch(() => { /* Preview servers have no /api/region: keep the manual switch. */ });

  /* ── Email ───────────────────────────────────────────────────────────── */
  function validEmail() {
    const value = email.value.trim();
    const ok = value.length > 0 && email.checkValidity() && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value);
    email.setAttribute('aria-invalid', String(!ok));
    say(emailError, ok ? '' : value ? 'That email doesn’t look complete. Check it and try again.' : 'Enter the email where you’d like your license key.', 'error');
    return ok;
  }
  email.addEventListener('input', () => { if (email.getAttribute('aria-invalid') === 'true') validEmail(); });

  /* ── Pay ─────────────────────────────────────────────────────────────── */
  function setBusy(next, label) {
    busy = next;
    payButton.disabled = next;
    payButton.setAttribute('aria-busy', String(next));
    tabInr.disabled = next;
    tabUsd.disabled = next;
    for (const button of planButtons) button.disabled = next;
    payLabel.textContent = label || `Pay ${priceLabel(plan, region)}`;
  }

  async function postJson(url, body) {
    const response = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const result = await response.json().catch(() => ({}));
    return { response, result };
  }

  form.addEventListener('submit', async event => {
    event.preventDefault();
    say(status, '');
    if (!validEmail()) { email.focus(); return; }
    if (typeof window.Razorpay !== 'function') {
      say(status, 'Secure checkout couldn’t load. Check your connection or pause content blockers for this page, then try again.', 'error');
      return;
    }
    const customerEmail = email.value.trim();
    const chosen = { region, plan };
    const def = PLANS[chosen.plan];
    setBusy(true, 'Opening secure checkout…');
    try {
      let options;
      if (isSubscription(chosen.plan)) {
        const { response, result } = await postJson('/api/createSubscription', { email: customerEmail, deviceId, region: chosen.region, plan: chosen.plan });
        const subscriptionId = result.subscription_id || result.subscriptionId;
        if (!response.ok || !subscriptionId) throw new Error(result.error || 'We couldn’t start checkout. Please try again in a moment.');
        options = {
          key: result.key_id || result.keyId,
          subscription_id: subscriptionId,
          name: 'FlyShelf Pro',
          description: `${def.title.replace('FlyShelf ', '')} — renews every ${def.period} until you cancel`,
          handler: payment => confirmPayment(payment, customerEmail, chosen),
        };
      } else {
        const { response, result } = await postJson('/api/createOrder', { email: customerEmail, deviceId, region: chosen.region });
        if (!response.ok) throw new Error(result.error || 'We couldn’t start checkout. Please try again in a moment.');
        options = {
          key: result.keyId,
          amount: result.amount,
          currency: result.currency,
          order_id: result.orderId,
          name: 'FlyShelf Pro',
          description: 'Lifetime license — one-time payment',
          handler: payment => confirmPayment(payment, customerEmail, chosen),
        };
      }
      const checkout = new window.Razorpay({
        ...options,
        prefill: { email: customerEmail },
        theme: { color: '#2456d3' },
        modal: {
          ondismiss() {
            setBusy(false);
            say(status, 'Checkout closed. You haven’t been charged — pay whenever you’re ready.');
          },
        },
      });
      checkout.on('payment.failed', response => {
        setBusy(false);
        const reason = response?.error?.description;
        say(status, `Payment didn’t go through${reason ? `: ${reason}` : '.'} You can try again or use another method.`, 'error');
      });
      checkout.open();
    } catch (error) {
      setBusy(false);
      say(status, error.message || 'We couldn’t start checkout. Please try again.', 'error');
    }
  });

  async function confirmPayment(payment, customerEmail, chosen) {
    openModal('loading');
    $('#loader-headline').textContent = 'Payment received';
    $('#loader-desc').textContent = 'Generating your license key. Please keep this tab open.';
    const subscription = isSubscription(chosen.plan);
    try {
      const { response, result } = subscription
        ? await postJson('/api/verifySubscription', {
          razorpay_payment_id: payment.razorpay_payment_id,
          razorpay_subscription_id: payment.razorpay_subscription_id,
          razorpay_signature: payment.razorpay_signature,
          email: customerEmail,
          deviceId,
        })
        : await postJson('/api/verifyPayment', {
          razorpay_payment_id: payment.razorpay_payment_id,
          razorpay_order_id: payment.razorpay_order_id,
          razorpay_signature: payment.razorpay_signature,
          email: customerEmail,
          deviceId,
        });
      if (!response.ok || !result.success || !result.licenseKey) throw new Error(result.error || `Server returned ${response.status}`);
      generatedKey = result.licenseKey;
      try {
        sessionStorage.setItem('flyshelf_recent_purchase', JSON.stringify({ key: generatedKey, email: customerEmail, timestamp: Date.now() }));
      } catch { /* The key is still shown and emailed. */ }
      showRecentOrder();
      keyOutput.textContent = generatedKey;
      $('#confirm-email').textContent = customerEmail;
      const planNote = $('#confirm-plan');
      if (subscription) {
        const renews = formatDate(result.currentPeriodEnd);
        planNote.textContent = `${PLANS[chosen.plan].title.replace('FlyShelf ', '')}${renews ? ` · renews on ${renews}` : ''}. Stop renewal any time under Manage subscription on this page.`;
        planNote.hidden = false;
      } else {
        planNote.hidden = true;
      }
      showStage('success');
      celebrate();
      say(status, 'Payment complete. Your license key is below and in your email.', 'success');
    } catch (error) {
      console.error(error);
      showStage('error');
    } finally {
      setBusy(false);
    }
  }

  /* ── Dialog ──────────────────────────────────────────────────────────── */
  function showStage(name) {
    for (const [key, stage] of Object.entries(stages)) stage.hidden = key !== name;
    modal.dataset.stage = name;
    const focusTarget = name === 'success' ? keyOutput : name === 'error' ? $('#btn-close-error') : dialog;
    focusTarget.focus({ preventScroll: true });
  }
  function openModal(stage) {
    if (modal.hidden) lastFocus = document.activeElement;
    modal.hidden = false;
    document.documentElement.classList.add('modal-open');
    showStage(stage);
  }
  function closeModal() {
    if (modal.dataset.stage === 'loading') return; // Never abandon a payment that's being confirmed.
    modal.hidden = true;
    document.documentElement.classList.remove('modal-open');
    // The pay button is disabled while busy, so focus usually falls back to it.
    const returnTo = lastFocus && lastFocus !== document.body && document.contains(lastFocus) && !lastFocus.disabled ? lastFocus : payButton;
    returnTo.focus({ preventScroll: true });
  }
  $('#btn-close-modal').addEventListener('click', closeModal);
  $('#btn-close-error').addEventListener('click', closeModal);
  modal.addEventListener('click', event => { if (event.target === modal) closeModal(); });
  document.addEventListener('keydown', event => {
    if (modal.hidden) return;
    if (event.key === 'Escape') { event.preventDefault(); closeModal(); return; }
    if (event.key !== 'Tab') return;
    // Keep keyboard focus inside the dialog.
    const focusable = [...dialog.querySelectorAll('button, a[href], input')].filter(node => !node.closest('[hidden]') && !node.disabled);
    if (!focusable.length) { event.preventDefault(); return; }
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && (document.activeElement === first || document.activeElement === dialog)) { event.preventDefault(); last.focus(); }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
  });

  /* ── Copy and activate ───────────────────────────────────────────────── */
  async function copyText(text) {
    try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
  }
  function flash(button, message, ok = true) {
    const label = button.querySelector('span') || button;
    if (!button.dataset.label) button.dataset.label = label.textContent;
    label.textContent = message;
    button.classList.toggle('is-done', ok);
    clearTimeout(button._flash);
    button._flash = setTimeout(() => { label.textContent = button.dataset.label; button.classList.remove('is-done'); }, 2400);
  }
  async function copyKey(button, key) {
    if (!key) return;
    const ok = await copyText(key);
    flash(button, ok ? 'Copied' : 'Select and copy the key', ok);
    window.FlyShelfSite?.toast(ok ? 'License key copied.' : 'Clipboard access is blocked. Select the key and copy it manually.');
  }
  // The FlyShelf PC app watches the clipboard for this prefix and activates Pro by itself.
  async function activate(button, key) {
    if (!key) return;
    if (await copyText(`FLYSHELF_ACTIVATE::${key}`)) {
      flash(button, 'Sent — check FlyShelf');
      window.FlyShelfSite?.toast('Activation code copied. FlyShelf will turn on Pro if it’s running.');
    } else {
      const copied = await copyText(key);
      flash(button, copied ? 'Key copied — paste it in FlyShelf' : 'Copy the key manually', copied);
    }
  }
  keyOutput.addEventListener('click', () => copyKey($('#btn-copy-license'), generatedKey));
  $('#btn-copy-license').addEventListener('click', event => copyKey(event.currentTarget, generatedKey));
  $('#btn-activate-deeplink').addEventListener('click', event => activate(event.currentTarget, generatedKey));

  /* ── Celebration (skipped when motion is reduced or paused) ──────────── */
  function celebrate() {
    const container = $('#confetti-container');
    if (!container || !motionOK()) return;
    container.replaceChildren();
    const colors = ['#2456d3', '#58d2df', '#6ee7b7', '#f6c445', '#b4a2ff', '#ffb08c'];
    for (let i = 0; i < 36; i++) {
      const piece = document.createElement('i');
      piece.className = 'confetti-particle';
      piece.style.left = `${Math.random() * 100}%`;
      piece.style.background = colors[i % colors.length];
      piece.style.width = piece.style.height = `${4 + Math.random() * 5}px`;
      piece.style.borderRadius = Math.random() > .5 ? '50%' : '2px';
      piece.style.animationDuration = `${1.6 + Math.random() * 1.6}s`;
      piece.style.animationDelay = `${Math.random() * .5}s`;
      container.append(piece);
    }
    setTimeout(() => container.replaceChildren(), 4200);
  }

  /* ── Recent purchase (this tab only, 15 minutes) ─────────────────────── */
  const recent = $('#recent-order-section');
  const recentKey = $('#recent-order-key');
  const recentTimer = $('#recent-order-timer');
  const recentProgress = $('#recent-order-progress');
  const EXPIRY_MS = 15 * 60 * 1000;
  let recentInterval = 0;
  function clearRecentOrder() {
    try { sessionStorage.removeItem('flyshelf_recent_purchase'); } catch { /* Nothing stored. */ }
    clearInterval(recentInterval);
    recent.hidden = true;
  }
  function showRecentOrder() {
    let cached = null;
    try { cached = JSON.parse(sessionStorage.getItem('flyshelf_recent_purchase')); } catch { return; }
    if (!cached?.key) return;
    if (Date.now() - cached.timestamp >= EXPIRY_MS) { clearRecentOrder(); return; }
    recentKey.textContent = cached.key;
    recent.hidden = false;
    clearInterval(recentInterval);
    const tick = () => {
      const remaining = EXPIRY_MS - (Date.now() - cached.timestamp);
      if (remaining <= 0) { clearRecentOrder(); return; }
      const minutes = Math.floor(remaining / 60000);
      const seconds = String(Math.floor((remaining % 60000) / 1000)).padStart(2, '0');
      recentTimer.textContent = `This box hides itself in ${minutes}:${seconds}.`;
      recentProgress.style.transform = `scaleX(${remaining / EXPIRY_MS})`;
      recent.classList.toggle('is-ending', remaining / EXPIRY_MS < .2);
    };
    tick();
    recentInterval = setInterval(tick, 1000);
  }
  showRecentOrder();
  $('#btn-dismiss-recent').addEventListener('click', clearRecentOrder);
  recentKey.addEventListener('click', () => copyKey($('#btn-copy-recent'), recentKey.textContent));
  $('#btn-copy-recent').addEventListener('click', event => copyKey(event.currentTarget, recentKey.textContent));
  $('#recent-order-deeplink').addEventListener('click', event => activate(event.currentTarget, recentKey.textContent));

  /* ── License recovery (api/recoverKey emails the key; it never returns it) ─ */
  const recoverForm = $('#recover-form');
  const recoverEmail = $('#recover-email');
  const recoverButton = $('#recover-submit');
  const recoverStatus = $('#recover-status');
  recoverForm?.addEventListener('submit', async event => {
    event.preventDefault();
    const value = recoverEmail.value.trim();
    if (!value || !recoverEmail.checkValidity() || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value)) {
      recoverEmail.setAttribute('aria-invalid', 'true');
      say(recoverStatus, 'Enter the email address you used at checkout.', 'error');
      recoverEmail.focus();
      return;
    }
    recoverEmail.setAttribute('aria-invalid', 'false');
    recoverButton.disabled = true;
    recoverButton.setAttribute('aria-busy', 'true');
    say(recoverStatus, 'Sending…');
    try {
      const response = await fetch('/api/recoverKey', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email: value }),
      });
      const result = await response.json().catch(() => ({}));
      // Purchases aren't confirmed or denied here, so the answer is the same either way.
      if (response.ok || response.status === 404) say(recoverStatus, `If ${value} was used to buy FlyShelf Pro, the license key is on its way there now. Check spam if it hasn’t arrived in a few minutes.`, 'success');
      else if (response.status === 429) say(recoverStatus, result.error || 'Too many requests for this email. Please try again tomorrow.', 'error');
      else say(recoverStatus, 'We couldn’t send the email just now. Please try again in a few minutes.', 'error');
    } catch {
      say(recoverStatus, 'You seem to be offline. Check your connection and try again.', 'error');
    } finally {
      recoverButton.disabled = false;
      recoverButton.removeAttribute('aria-busy');
    }
  });

  /* ── Manage subscription (api/manageSubscription; key + email must both match) ─ */
  const accountForm = $('#account-form');
  const accountKey = $('#account-key');
  const accountEmail = $('#account-email');
  const accountSubmit = $('#account-submit');
  const accountStatus = $('#account-status');
  const accountResult = $('#account-result');
  const cancelButton = $('#account-cancel');
  const confirmBox = $('#account-confirm');
  const confirmYes = $('#account-confirm-yes');
  const STATUS_TEXT = {
    active: 'Active', past_due: 'Payment retrying', halted: 'Paused — payment failed', paused: 'Paused',
    cancelled: 'Cancelled', completed: 'Ended', expired: 'Ended', revoked: 'Revoked', created: 'Not started',
  };
  let lookup = null;

  function showAccount(data) {
    lookup = data;
    accountResult.hidden = false;
    confirmBox.hidden = true;
    $('#account-plan').textContent = data.planLabel || 'Pro';
    const lifetime = data.plan === 'lifetime';
    const ended = ['cancelled', 'completed', 'expired', 'revoked'].includes(data.status);
    $('#account-state').textContent = data.cancelAtPeriodEnd ? 'Active — won’t renew' : STATUS_TEXT[data.status] || data.status;
    const date = formatDate(data.currentPeriodEnd);
    $('#account-date-row').hidden = lifetime || !date;
    $('#account-date-label').textContent = data.renewsAt ? 'Renews on' : ended && Date.parse(data.currentPeriodEnd) < Date.now() ? 'Ended on' : 'Pro until';
    $('#account-date').textContent = date;
    cancelButton.hidden = lifetime || ended || data.cancelAtPeriodEnd || data.status === 'halted';
    $('#account-confirm-date').textContent = date || 'the end of your current period';
  }

  async function manage(action) {
    const key = accountKey.value.trim().toUpperCase();
    const value = accountEmail.value.trim();
    const keyOk = /^FS-PRO-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$/.test(key);
    const emailOk = value && accountEmail.checkValidity() && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value);
    accountKey.setAttribute('aria-invalid', String(!keyOk));
    accountEmail.setAttribute('aria-invalid', String(!emailOk));
    if (!keyOk || !emailOk) {
      say(accountStatus, !keyOk ? 'Enter your license key — it looks like FS-PRO-XXXX-XXXX-XXXX-XXXX.' : 'Enter the email you used at checkout.', 'error');
      (keyOk ? accountEmail : accountKey).focus();
      return;
    }
    accountSubmit.disabled = true;
    confirmYes.disabled = true;
    say(accountStatus, action === 'cancel' ? 'Stopping renewal…' : 'Looking up your plan…');
    try {
      const { response, result } = await postJson('/api/manageSubscription', { key, email: value, action });
      if (response.ok && result.success) {
        showAccount(result);
        if (action === 'cancel') {
          const until = formatDate(result.currentPeriodEnd);
          say(accountStatus, `Renewal stopped. You won’t be charged again${until ? `, and Pro stays on until ${until}` : ''}.`, 'success');
          accountSubmit.focus();
        } else {
          say(accountStatus, result.plan === 'lifetime' ? 'This is a lifetime key — there’s nothing to renew or cancel.' : '');
        }
      } else if (response.status === 404) {
        accountResult.hidden = true;
        say(accountStatus, 'We couldn’t find a plan for that key and email. Check both and try again.', 'error');
      } else if (response.status === 429) {
        say(accountStatus, result.message || 'Too many requests. Please try again later.', 'error');
      } else {
        say(accountStatus, result.message || 'We couldn’t reach the server just now. Please try again in a few minutes.', 'error');
      }
    } catch {
      say(accountStatus, 'You seem to be offline. Check your connection and try again.', 'error');
    } finally {
      accountSubmit.disabled = false;
      confirmYes.disabled = false;
    }
  }

  accountForm?.addEventListener('submit', event => { event.preventDefault(); manage('status'); });
  cancelButton?.addEventListener('click', () => {
    if (!lookup) return;
    confirmBox.hidden = false;
    cancelButton.hidden = true;
    $('#account-confirm-no').focus();
  });
  $('#account-confirm-no')?.addEventListener('click', () => {
    confirmBox.hidden = true;
    cancelButton.hidden = false;
    cancelButton.focus();
  });
  confirmYes?.addEventListener('click', () => manage('cancel'));
})();
