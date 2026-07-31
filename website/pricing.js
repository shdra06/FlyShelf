    document.addEventListener('DOMContentLoaded', () => {

      // XSS sanitization helper (security audit v3.0)
      function escapeHTML(str) {
        if (!str) return '';
        const d = document.createElement('div');
        d.textContent = String(str);
        return d.innerHTML;
      }

      
      /* Glass blur lazy visibility (performance) */
      const glassEls = document.querySelectorAll('.glass');
      const glassObs = new IntersectionObserver((entries) => {
        entries.forEach(e => e.target.classList.toggle('glass--visible', e.isIntersecting));
      }, { rootMargin: '200px 0px 200px 0px', threshold: 0 });
      glassEls.forEach(el => glassObs.observe(el));

      const tabInr = document.getElementById('tab-inr');
      const tabUsd = document.getElementById('tab-usd');
      const priceDisplayUsd = document.getElementById('price-display-usd');
      const priceDisplayInr = document.getElementById('price-display-inr');
      const razorpayDetails = document.getElementById('razorpay-details');
      const paypalDetails = document.getElementById('paypal-details');
      const btnSubmitPayment = document.getElementById('btn-submit-payment');
      
      const checkoutModal = document.getElementById('checkout-modal');
      const modalStageLoading = document.getElementById('modal-stage-loading');
      const modalStageApproval = document.getElementById('modal-stage-approval');
      const modalStageSuccess = document.getElementById('modal-stage-success');
      const loaderHeadline = document.getElementById('loader-headline');
      const loaderDesc = document.getElementById('loader-desc');
      
      const customerEmail = document.getElementById('customer-email');
      const confirmEmail = document.getElementById('confirm-email');
      const confirmAmount = document.getElementById('confirm-amount');
      
      const licenseKeyOutput = document.getElementById('license-key-output');
      const btnCopyLicense = document.getElementById('btn-copy-license');
      const btnActivateDeeplink = document.getElementById('btn-activate-deeplink');
      const btnCloseModal = document.getElementById('btn-close-modal');
      
      let selectedRegion = 'USD';
      let generatedKey = '';

      // Get deviceId from URL params (passed from desktop app)
      const urlParams = new URLSearchParams(window.location.search);
      const deviceId = urlParams.get('deviceId') || 'web_purchase';

      // ═══════════════════════════════════════════
      // LIGHT/DARK MODE SYSTEM
      // ═══════════════════════════════════════════
      const modeToggleBtn = document.getElementById('theme-toggle-btn');
      const modeToggleIcon = document.getElementById('theme-toggle-icon');
      
      let currentMode = localStorage.getItem('flyshelf-mode') || 'light';
      applyMode(currentMode);
      
      if (modeToggleBtn) {
        modeToggleBtn.addEventListener('click', () => {
          currentMode = currentMode === 'light' ? 'dark' : 'light';
          applyMode(currentMode);
        });
      }
      
      function applyMode(mode) {
        if (mode === 'dark') {
          document.body.classList.add('dark-mode');
          if (modeToggleIcon) modeToggleIcon.setAttribute('name', 'sunny-outline');
        } else {
          document.body.classList.remove('dark-mode');
          if (modeToggleIcon) modeToggleIcon.setAttribute('name', 'moon-outline');
        }
        localStorage.setItem('flyshelf-mode', mode);
      }

      // Also apply workspace swatches if saved
      const savedTheme = localStorage.getItem('flyshelf-theme') || 'midnight';
      if (savedTheme !== 'midnight') {
        document.body.classList.add(`theme-${savedTheme}`);
      }

      // Set reveal trigger
      setTimeout(() => {
        document.querySelectorAll('.scroll-reveal').forEach(el => el.classList.add('visible'));
      }, 100);

      // ═══════════════════════════════════════════
      // REGION TAB SWITCHING
      // ═══════════════════════════════════════════

      tabInr.addEventListener('click', () => {
        selectedRegion = 'INR';
        tabInr.classList.add('active');
        tabUsd.classList.remove('active');
        priceDisplayInr.style.display = 'inline';
        priceDisplayUsd.style.display = 'none';
        razorpayDetails.style.display = 'flex';
        paypalDetails.style.display = 'none';
        
        btnSubmitPayment.className = 'btn-checkout-pay btn-razorpay';
        btnSubmitPayment.innerHTML = '<ion-icon name="card-outline"></ion-icon> Pay ₹299 via Razorpay';
      });

      tabUsd.addEventListener('click', () => {
        selectedRegion = 'USD';
        tabUsd.classList.add('active');
        tabInr.classList.remove('active');
        priceDisplayUsd.style.display = 'inline';
        priceDisplayInr.style.display = 'none';
        paypalDetails.style.display = 'flex';
        razorpayDetails.style.display = 'none';
        
        btnSubmitPayment.className = 'btn-checkout-pay btn-razorpay';
        btnSubmitPayment.innerHTML = '<ion-icon name="card-outline"></ion-icon> Pay $9.99 via Card';
      });

      // ═══════════════════════════════════════════
      // PAYMENT BUTTON CLICK
      // ═══════════════════════════════════════════

      btnSubmitPayment.addEventListener('click', () => {
        const emailVal = customerEmail.value.trim();
        if (!emailVal || !emailVal.includes('@')) {
          alert('Please enter a valid email address.');
          return;
        }

        // Both INR and USD use Razorpay — just different currencies
        openRazorpayCheckout(emailVal, selectedRegion);
      });

      // ═══════════════════════════════════════════
      // RAZORPAY STANDARD CHECKOUT
      // ═══════════════════════════════════════════

      async function openRazorpayCheckout(email, region) {
        // Disable button to prevent double-click
        btnSubmitPayment.disabled = true;
        btnSubmitPayment.innerHTML = '<div class="payment-loader" style="width:20px;height:20px;border-width:2px;margin:0;"></div> Processing...';

        try {
          const backendUrl = window.location.origin;
          const orderRes = await fetch(`${backendUrl}/api/createOrder`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ email, deviceId, region })
          });

          if (!orderRes.ok) {
            const errData = await orderRes.json().catch(() => ({}));
            throw new Error(errData.error || 'Failed to create payment order. Please try again.');
          }

          const orderData = await orderRes.json();

          const options = {
            key: orderData.keyId,
            amount: orderData.amount,
            currency: orderData.currency,
            name: 'FlyShelf Pro',
            description: 'Lifetime Pro License — One-time Payment',
            order_id: orderData.orderId,
            prefill: {
              email: email
            },
            theme: {
              color: '#0b72e7'
            },
            handler: async function(response) {
              // ── PAYMENT SUCCESS ──
              console.log('Payment successful:', response.razorpay_payment_id);
              
              // Show processing modal
              checkoutModal.style.display = 'flex';
              modalStageLoading.style.display = 'flex';
              modalStageApproval.style.display = 'none';
              modalStageSuccess.style.display = 'none';
              loaderHeadline.textContent = 'Payment Captured!';
              loaderDesc.textContent = 'Generating your license key...';

              try {
                // Verify signature on backend
                const verifyRes = await fetch(`${backendUrl}/api/verifyPayment`, {
                  method: 'POST',
                  headers: { 'Content-Type': 'application/json' },
                  body: JSON.stringify({
                    razorpay_payment_id: response.razorpay_payment_id,
                    razorpay_order_id: response.razorpay_order_id,
                    razorpay_signature: response.razorpay_signature,
                    email: email,
                    deviceId: deviceId
                  })
                });

                const verifyData = await verifyRes.json();

                if (!verifyRes.ok) {
                  throw new Error(verifyData.error || `Server returned ${verifyRes.status}`);
                }

                if (verifyData.success) {
                  generatedKey = verifyData.licenseKey;
                  
                  // Cache key in sessionStorage for 15 minutes
                  try {
                    sessionStorage.setItem('flyshelf_recent_purchase', JSON.stringify({
                      key: generatedKey,
                      email: email,
                      timestamp: Date.now()
                    }));
                    showRecentOrder();
                  } catch(e) {}
                  
                  // Show success modal
                  modalStageLoading.style.display = 'none';
                  modalStageSuccess.style.display = 'flex';
                  licenseKeyOutput.textContent = generatedKey;
                  btnActivateDeeplink.href = `flyshelf://activate?key=${generatedKey}`;
                  confirmEmail.textContent = email;
                  confirmAmount.textContent = region === 'USD' ? '$9.99' : '₹299';
                  spawnConfetti();
                } else {
                  alert('Verification could not be completed. Your payment is safe — please contact support@flyshelf.app');
                  modalStageLoading.style.display = 'none';
                }
              } catch (verifyErr) {
                console.error(verifyErr);
                alert('Something went wrong verifying your payment. Your money is safe — please contact support@flyshelf.app if your key doesn\'t arrive by email.');
                modalStageLoading.style.display = 'none';
              }

              // Reset button
              btnSubmitPayment.disabled = false;
              btnSubmitPayment.innerHTML = selectedRegion === 'USD' ? '<ion-icon name="card-outline"></ion-icon> Pay $9.99 via Card' : '<ion-icon name="card-outline"></ion-icon> Pay ₹299 via Razorpay';
            },
            modal: {
              ondismiss: function() {
                // User closed the Razorpay popup
                btnSubmitPayment.disabled = false;
                btnSubmitPayment.innerHTML = selectedRegion === 'USD' ? '<ion-icon name="card-outline"></ion-icon> Pay $9.99 via Card' : '<ion-icon name="card-outline"></ion-icon> Pay ₹299 via Razorpay';
              }
            }
          };

          const rzp = new Razorpay(options);
          
          rzp.on('payment.failed', function(response) {
            console.error('Payment failed:', response.error);
            alert(`Payment failed: ${response.error.description}\n\nPlease try again.`);
            btnSubmitPayment.disabled = false;
            btnSubmitPayment.innerHTML = selectedRegion === 'USD' ? '<ion-icon name="card-outline"></ion-icon> Pay $9.99 via Card' : '<ion-icon name="card-outline"></ion-icon> Pay ₹299 via Razorpay';
          });

          rzp.open();

        } catch (err) {
          console.error(err);
          alert(err.message || 'Could not initialize payment. Please try again.');
          btnSubmitPayment.disabled = false;
          btnSubmitPayment.innerHTML = selectedRegion === 'USD' ? '<ion-icon name="card-outline"></ion-icon> Pay $9.99 via Card' : '<ion-icon name="card-outline"></ion-icon> Pay ₹299 via Razorpay';
        }
      }

      // ═══════════════════════════════════════════
      // COPY LICENSE KEY
      // ═══════════════════════════════════════════

      // Click on the key box itself to copy
      licenseKeyOutput.addEventListener('click', () => {
        copyAndFlash(licenseKeyOutput, generatedKey);
      });

      btnCopyLicense.addEventListener('click', () => {
        copyAndFlash(btnCopyLicense, generatedKey);
      });

      // Reusable copy + flash feedback
      function copyAndFlash(el, text) {
        navigator.clipboard.writeText(text);
        const original = el.innerHTML;
        const origBg = el.style.background;
        const origColor = el.style.color;
        el.innerHTML = '<ion-icon name="checkmark-done-outline" style="vertical-align: middle;"></ion-icon> Copied!';
        el.style.background = '#3ddc84';
        el.style.color = '#000';
        setTimeout(() => {
          el.innerHTML = original;
          el.style.background = origBg;
          el.style.color = origColor;
        }, 1800);
      }

      // ═══════════════════════════════════════════
      // CONFETTI CELEBRATION EFFECT
      // ═══════════════════════════════════════════
      function spawnConfetti() {
        const container = document.getElementById('confetti-container');
        if (!container) return;
        container.innerHTML = ''; // Clear any previous confetti
        const colors = ['#00d2ff', '#3ddc84', '#ff9d00', '#a78bfa', '#f472b6', '#fbbf24', '#34d399'];
        const count = 40;
        for (let i = 0; i < count; i++) {
          const particle = document.createElement('div');
          particle.className = 'confetti-particle';
          particle.style.left = Math.random() * 100 + '%';
          particle.style.background = colors[Math.floor(Math.random() * colors.length)];
          particle.style.width = (Math.random() * 6 + 4) + 'px';
          particle.style.height = (Math.random() * 6 + 4) + 'px';
          particle.style.animationDuration = (Math.random() * 2 + 1.5) + 's';
          particle.style.animationDelay = (Math.random() * 0.8) + 's';
          particle.style.borderRadius = Math.random() > 0.5 ? '50%' : '2px';
          container.appendChild(particle);
        }
        // Auto-clean after animation completes
        setTimeout(() => { container.innerHTML = ''; }, 4000);
      }

      // ═══════════════════════════════════════════
      // DEEP LINK ACTIVATE — copy activation trigger to clipboard
      // The FlyShelf PC app monitors clipboard for the FLYSHELF_ACTIVATE:: prefix
      // and auto-triggers activation when detected.
      // ═══════════════════════════════════════════
      function tryActivate(key, feedbackEl) {
        // Copy key with activation trigger prefix — FlyShelf PC app auto-detects this
        const activationString = `FLYSHELF_ACTIVATE::${key}`;
        navigator.clipboard.writeText(activationString).then(() => {
          // Visual feedback on button
          if (feedbackEl) {
            const orig = feedbackEl.innerHTML;
            feedbackEl.innerHTML = '<ion-icon name="checkmark-done-outline" style="font-size: 1rem;"></ion-icon> Activating... Check your PC app!';
            feedbackEl.style.pointerEvents = 'none';
            feedbackEl.style.background = 'linear-gradient(135deg, var(--color-emerald), #0ea5e9)';
            setTimeout(() => {
              feedbackEl.innerHTML = orig;
              feedbackEl.style.pointerEvents = '';
              feedbackEl.style.background = '';
            }, 4000);
          }
        }).catch(() => {
          // Fallback: copy plain key
          navigator.clipboard.writeText(key).catch(() => {});
          if (feedbackEl) {
            const orig = feedbackEl.innerHTML;
            feedbackEl.innerHTML = '<ion-icon name="copy-outline" style="font-size: 1rem;"></ion-icon> Key Copied! Paste in app settings.';
            setTimeout(() => { feedbackEl.innerHTML = orig; }, 3000);
          }
        });
      }

      // Success modal activate button
      btnActivateDeeplink.addEventListener('click', (e) => {
        e.preventDefault();
        if (generatedKey) tryActivate(generatedKey, btnActivateDeeplink);
      });

      // Close modal
      btnCloseModal.addEventListener('click', () => {
        checkoutModal.style.display = 'none';
      });

      checkoutModal.addEventListener('click', (e) => {
        if (e.target === checkoutModal) {
          checkoutModal.style.display = 'none';
        }
      });

      // ═══════════════════════════════════════════
      // KEY GENERATION — Server-side only (security hardened v2.0.0)
      // All license key generation now happens exclusively on the
      // Vercel backend (api/verifyPayment.js) after payment verification.
      // HMAC secret is NEVER exposed to the client.
      // ═══════════════════════════════════════════

      // ═══════════════════════════════════════════
      // RECENTLY ORDERED — sessionStorage cache (15 min)
      // ═══════════════════════════════════════════
      const recentOrderSection = document.getElementById('recent-order-section');
      const recentOrderKey = document.getElementById('recent-order-key');
      const recentOrderTimer = document.getElementById('recent-order-timer');
      const btnCopyRecent = document.getElementById('btn-copy-recent');
      const recentOrderDeeplink = document.getElementById('recent-order-deeplink');

      let recentTimerInterval = null;

      const recentProgressBar = document.getElementById('recent-order-progress');
      const btnDismissRecent = document.getElementById('btn-dismiss-recent');
      const EXPIRY_MS = 15 * 60 * 1000; // 15 minutes

      function clearRecentOrder() {
        sessionStorage.removeItem('flyshelf_recent_purchase');
        if (recentTimerInterval) clearInterval(recentTimerInterval);
        // Fade out smoothly
        recentOrderSection.style.transition = 'opacity 0.4s ease, transform 0.4s ease';
        recentOrderSection.style.opacity = '0';
        recentOrderSection.style.transform = 'translateY(-10px)';
        setTimeout(() => { recentOrderSection.style.display = 'none'; }, 400);
      }

      function showRecentOrder() {
        try {
          const cached = JSON.parse(sessionStorage.getItem('flyshelf_recent_purchase'));
          if (!cached || !cached.key) return;
          
          const elapsed = Date.now() - cached.timestamp;
          
          if (elapsed >= EXPIRY_MS) {
            clearRecentOrder();
            return;
          }
          
          recentOrderKey.textContent = cached.key;
          recentOrderDeeplink.href = `flyshelf://activate?key=${cached.key}`;
          recentOrderSection.style.display = 'block';
          recentOrderSection.style.opacity = '1';
          recentOrderSection.style.transform = 'translateY(0)';
          
          // Update progress bar + countdown every second
          if (recentTimerInterval) clearInterval(recentTimerInterval);
          function tick() {
            const remaining = EXPIRY_MS - (Date.now() - cached.timestamp);
            if (remaining <= 0) {
              clearRecentOrder();
              return;
            }
            const mins = Math.floor(remaining / 60000);
            const secs = Math.floor((remaining % 60000) / 1000);
            recentOrderTimer.textContent = `Expires in ${mins}:${secs.toString().padStart(2, '0')}`;
            
            // Progress bar percentage
            const pct = Math.max(0, (remaining / EXPIRY_MS) * 100);
            if (recentProgressBar) recentProgressBar.style.width = pct + '%';
            
            // Color shift: green → yellow → red as time runs low
            if (pct < 20) {
              if (recentProgressBar) recentProgressBar.style.background = 'linear-gradient(90deg, #ff6b6b, #ff4757)';
              recentOrderTimer.style.color = '#ff6b6b';
            } else if (pct < 40) {
              if (recentProgressBar) recentProgressBar.style.background = 'linear-gradient(90deg, #ffa502, #ff6348)';
              recentOrderTimer.style.color = '#ffa502';
            } else {
              if (recentProgressBar) recentProgressBar.style.background = 'linear-gradient(90deg, var(--color-emerald), var(--color-cyan))';
              recentOrderTimer.style.color = 'var(--color-cyan)';
            }
          }
          tick(); // Run immediately
          recentTimerInterval = setInterval(tick, 1000);
        } catch(e) {}
      }

      // Show cached order on page load
      showRecentOrder();

      // Dismiss button
      if (btnDismissRecent) {
        btnDismissRecent.addEventListener('click', clearRecentOrder);
      }

      // Click on recent key box to copy
      if (recentOrderKey) {
        recentOrderKey.addEventListener('click', () => {
          const key = recentOrderKey.textContent;
          copyAndFlash(recentOrderKey, key);
        });
      }

      // Copy button for recent order
      if (btnCopyRecent) {
        btnCopyRecent.addEventListener('click', () => {
          const key = recentOrderKey.textContent;
          navigator.clipboard.writeText(key);
          btnCopyRecent.innerHTML = '<ion-icon name="checkmark-done-outline" style="font-size: 1rem;"></ion-icon> Copied!';
          setTimeout(() => {
            btnCopyRecent.innerHTML = '<ion-icon name="copy-outline" style="font-size: 1rem;"></ion-icon> Copy Key';
          }, 1800);
        });
      }

      // Activate button for recent order
      if (recentOrderDeeplink) {
        recentOrderDeeplink.addEventListener('click', (e) => {
          e.preventDefault();
          const key = recentOrderKey.textContent;
          if (key) tryActivate(key, recentOrderDeeplink);
        });
      }
      // Mobile Navigation Drawer Toggle
      const mobileToggle = document.getElementById('mobile-menu-toggle');
      const mobileDrawer = document.getElementById('mobile-menu-drawer');
      const mobileClose = document.getElementById('mobile-menu-close');
      if (mobileToggle && mobileDrawer) {
        mobileToggle.addEventListener('click', () => {
          mobileDrawer.classList.toggle('open');
        });
      }
      if (mobileClose && mobileDrawer) {
        mobileClose.addEventListener('click', () => {
          mobileDrawer.classList.remove('open');
        });
      }
      const mobileLinks = document.querySelectorAll('.mobile-nav-links a');
      mobileLinks.forEach(link => {
        link.addEventListener('click', () => {
          mobileDrawer.classList.remove('open');
        });
      });

    });