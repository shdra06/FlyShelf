/* 
   FlyShelf - Premium Cross-Device Clipboard & Productivity Ecosystem
   Interactive Core Logic & Simulators (Vanilla JS)
*/

document.addEventListener('DOMContentLoaded', () => {

  /* ==========================================
     0.0.1 ANDROID DETECTION & APK CTA SWAP
     ========================================== */
  const isAndroid = /Android/i.test(navigator.userAgent);
  if (isAndroid) {
    document.body.classList.add('is-android');
    // Prioritize APK download
    const btnHeroDownloadExe = document.getElementById('btn-hero-download-exe');
    const btnHeroDownloadApk = document.getElementById('btn-hero-download-apk');
    if (btnHeroDownloadExe && btnHeroDownloadApk) {
      btnHeroDownloadApk.classList.add('btn-cyan');
      btnHeroDownloadApk.classList.remove('btn-outline');
      btnHeroDownloadExe.classList.add('btn-outline');
      btnHeroDownloadExe.classList.remove('btn-cyan');
      btnHeroDownloadApk.parentNode.insertBefore(btnHeroDownloadApk, btnHeroDownloadExe);
    }
    const btnFooterDownloadExe = document.getElementById('btn-footer-download-exe');
    const btnFooterDownloadApk = document.getElementById('btn-footer-download-apk');
    if (btnFooterDownloadExe && btnFooterDownloadApk) {
      btnFooterDownloadApk.classList.add('btn-cyan');
      btnFooterDownloadApk.classList.remove('btn-outline');
      btnFooterDownloadExe.classList.add('btn-outline');
      btnFooterDownloadExe.classList.remove('btn-cyan');
      btnFooterDownloadApk.parentNode.insertBefore(btnFooterDownloadApk, btnFooterDownloadExe);
    }
  }

  /* ==========================================
     0.0.2 MOBILE NAVIGATION MENU DRAWER
     ========================================== */
  const mobileToggleBtn = document.getElementById('mobile-menu-toggle');
  const mobileMenuDrawer = document.getElementById('mobile-menu-drawer');
  const mobileCloseBtn = document.getElementById('mobile-menu-close');
  
  if (mobileToggleBtn && mobileMenuDrawer) {
    mobileToggleBtn.addEventListener('click', () => {
      mobileMenuDrawer.classList.toggle('open');
    });
  }
  if (mobileCloseBtn && mobileMenuDrawer) {
    mobileCloseBtn.addEventListener('click', () => {
      mobileMenuDrawer.classList.remove('open');
    });
  }
  // Close drawer when clicking nav links
  const mobileDrawerLinks = document.querySelectorAll('.mobile-nav-links a, .mobile-menu-cta a');
  mobileDrawerLinks.forEach(link => {
    link.addEventListener('click', () => {
      if (mobileMenuDrawer) {
        mobileMenuDrawer.classList.remove('open');
      }
    });
  });

  /* ==========================================
     0. LIGHT/DARK THEME TOGGLER & PERSISTENCE
     ========================================== */
  const modeToggleBtn = document.getElementById('theme-toggle-btn');
  const modeToggleIcon = document.getElementById('theme-toggle-icon');
  
  let currentMode = 'light';
  try {
    currentMode = localStorage.getItem('flyshelf-mode') || 'light';
  } catch (err) {
    console.warn('LocalStorage access is blocked in this browser context:', err);
  }
  
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
    try {
      localStorage.setItem('flyshelf-mode', mode);
    } catch (err) {
      console.warn('Failed to persist flyshelf-mode setting to LocalStorage:', err);
    }
  }

  /* ==========================================
     0.1 BRAND WORKSPACE THEME SWITCHER
     ========================================== */
  const swatches = document.querySelectorAll('.theme-swatch');
  
  let savedTheme = 'midnight';
  try {
    savedTheme = localStorage.getItem('flyshelf-theme') || 'midnight';
  } catch (err) {
    console.warn('LocalStorage access for theme swatches is blocked:', err);
  }
  
  applyTheme(savedTheme);
  
  swatches.forEach(swatch => {
    swatch.addEventListener('click', () => {
      const theme = swatch.dataset.theme;
      applyTheme(theme);
    });
  });
  
  function applyTheme(themeName) {
    // Remove other theme-* classes from body while preserving dark-mode class
    const isDarkMode = document.body.classList.contains('dark-mode');
    document.body.className = '';
    if (isDarkMode) {
      document.body.classList.add('dark-mode');
    }
    if (themeName !== 'midnight') {
      document.body.classList.add(`theme-${themeName}`);
    }
    
    // Update active state in UI
    swatches.forEach(s => {
      if (s.dataset.theme === themeName) {
        s.classList.add('active');
      } else {
        s.classList.remove('active');
      }
    });
    
    try {
      localStorage.setItem('flyshelf-theme', themeName);
    } catch (err) {
      console.warn('Failed to persist flyshelf-theme setting to LocalStorage:', err);
    }
  }


  /* ==========================================
     1. SCROLL REVEAL UTILITY (INTERSECTION OBSERVER)
     ========================================== */
  const revealElements = document.querySelectorAll('.scroll-reveal');
  const revealObserver = new IntersectionObserver((entries, observer) => {
    entries.forEach(entry => {
      if (entry.isIntersecting) {
        entry.target.classList.add('visible');
        observer.unobserve(entry.target);
      }
    });
  }, {
    threshold: 0.1,
    rootMargin: '0px 0px -50px 0px'
  });

  revealElements.forEach(el => revealObserver.observe(el));


  /* ==========================================
     1.1. GLASS BLUR LAZY VISIBILITY (PERFORMANCE)
     Only apply expensive backdrop-filter blur to .glass
     elements that are currently within the viewport.
     ========================================== */
  const glassElements = document.querySelectorAll('.glass');
  const glassObserver = new IntersectionObserver((entries) => {
    entries.forEach(entry => {
      if (entry.isIntersecting) {
        entry.target.classList.add('glass--visible');
      } else {
        entry.target.classList.remove('glass--visible');
      }
    });
  }, {
    rootMargin: '200px 0px 200px 0px', // pre-load 200px above/below viewport
    threshold: 0
  });

  glassElements.forEach(el => glassObserver.observe(el));


  /* ==========================================
     2. THE UNIVERSAL SYNC PIPELINE SIMULATOR
     ========================================== */
  const pathwaySelectors = document.querySelectorAll('.selector-pill');
  const pcClipboardItems = document.querySelectorAll('#pc-clipboard-items .sync-card');
  const mobileFeed = document.getElementById('mobile-feed');
  const emptyStateNotice = document.getElementById('empty-state-notice');
  
  const flowPath = document.getElementById('flow-path');
  const flowParticle = document.getElementById('flow-particle');
  
  let activePathway = 'lan'; // default
  let isSyncing = false;

  // Sync pathway switching
  pathwaySelectors.forEach(selector => {
    selector.addEventListener('click', () => {
      if (isSyncing) return; // lock when transferring
      
      pathwaySelectors.forEach(s => s.classList.remove('active'));
      selector.classList.add('active');
      
      activePathway = selector.dataset.path;
      
      // Update SVG path properties
      flowPath.className.baseVal = `flow-line active-lane lane-${activePathway}`;
      
      // Update color settings of the particle
      if (activePathway === 'lan') {
        flowParticle.style.fill = 'var(--color-cyan)';
        flowParticle.style.filter = 'drop-shadow(0 0 8px var(--color-cyan))';
        flowPath.setAttribute('d', 'M 0 50 Q 100 0 200 50');
      } else if (activePathway === 'cloud') {
        flowParticle.style.fill = 'var(--color-purple)';
        flowParticle.style.filter = 'drop-shadow(0 0 8px var(--color-purple))';
        flowPath.setAttribute('d', 'M 0 50 Q 100 20 200 50');
      } else if (activePathway === 'firebase') {
        flowParticle.style.fill = 'var(--color-amber)';
        flowParticle.style.filter = 'drop-shadow(0 0 8px var(--color-amber))';
        flowPath.setAttribute('d', 'M 0 50 Q 100 80 200 50');
      }
    });
  });

  // PC clipboard items sync click
  pcClipboardItems.forEach(card => {
    card.addEventListener('click', () => {
      if (isSyncing) return;
      isSyncing = true;
      
      // Clear highlight on other cards
      pcClipboardItems.forEach(c => c.classList.remove('active-transfer'));
      card.classList.add('active-transfer');
      
      const content = card.dataset.content;
      const type = card.dataset.type;
      const badge = card.dataset.badge;
      
      animatePacket(() => {
        // Callback on packet arrival
        addCardToMobileFeed(content, type, badge);
        card.classList.remove('active-transfer');
        isSyncing = false;
      });
    });
  });

  // Path coordinate interpolation for particle flow
  function animatePacket(onComplete) {
    let startTime = null;
    const duration = 1200; // ms
    
    // Select control Y based on current pathway for high-performance Bezier calculation
    let controlY = 0;
    if (activePathway === 'cloud') {
      controlY = 20;
    } else if (activePathway === 'firebase') {
      controlY = 80;
    }
    
    flowParticle.style.opacity = '1';
    
    function tick(timestamp) {
      if (!startTime) startTime = timestamp;
      const elapsed = timestamp - startTime;
      const progress = Math.min(elapsed / duration, 1);
      
      // Calculate coordinates using exact Bezier math to bypass heavy layout queries (getPointAtLength)
      // x(t) = 200 * t
      // y(t) = 50 * (1 - t)^2 + 2 * controlY * (1 - t) * t + 50 * t^2
      const t = progress;
      const x = 200 * t;
      const y = 50 * (1 - t) * (1 - t) + 2 * controlY * (1 - t) * t + 50 * t * t;
      
      flowParticle.setAttribute('cx', x);
      flowParticle.setAttribute('cy', y);
      
      if (progress < 1) {
        requestAnimationFrame(tick);
      } else {
        flowParticle.style.opacity = '0';
        onComplete();
      }
    }
    
    requestAnimationFrame(tick);
  }

  // Push new synced item to Android view mockup
  function addCardToMobileFeed(content, type, badge) {
    if (emptyStateNotice) {
      emptyStateNotice.style.display = 'none';
    }
    
    const card = document.createElement('div');
    card.className = 'phone-card';
    
    let typeClass = 'badge-cyan';
    if (badge === 'LINK') typeClass = 'badge-purple';
    if (badge === 'PDF') typeClass = 'badge-amber';
    
    card.innerHTML = `
      <div style="display:flex; justify-content:space-between; margin-bottom:5px;">
        <span class="badge ${typeClass}" style="font-size:0.6rem;">${badge}</span>
        <span style="font-size:0.6rem; color:var(--text-muted);">Just Now</span>
      </div>
      <div style="font-weight:700; color:#fff; word-break:break-all;">${content}</div>
      <div style="font-size:0.62rem; color:var(--text-secondary); margin-top:2px;">Received via ${activePathway.toUpperCase()}</div>
    `;
    
    mobileFeed.insertBefore(card, mobileFeed.firstChild);
  }


  /* ==========================================
     3. "MOUSE SHAKE" SUMMON GESTURE SIMULATOR
     ========================================== */
  const gestureSandbox = document.getElementById('gesture-sandbox');
  const gestureInstructionBox = document.getElementById('gesture-instruction-box');
  const shakeGauge = document.getElementById('shake-gauge');
  const winSumoOverlay = document.getElementById('win-sumo-overlay');
  const btnCloseSumo = document.getElementById('btn-close-sumo');
  
  let isTrackingShake = false;
  let shakeAccumulator = 0;
  let lastMouseX = null;
  let lastDirection = 0; // -1 = Left, 1 = Right, 0 = Static
  let directionsSwitched = 0;
  let decayTimer = null;

  gestureSandbox.addEventListener('mousedown', (e) => {
    // Stop event if inside elements of the overlay
    if (winSumoOverlay.classList.contains('unlocked') && e.target.closest('#win-sumo-overlay')) {
      return;
    }
    
    isTrackingShake = true;
    shakeAccumulator = 0;
    directionsSwitched = 0;
    lastMouseX = e.clientX;
    lastDirection = 0;
    gestureSandbox.classList.add('shaking');
    
    // Decay gauge over time
    if (decayTimer) clearInterval(decayTimer);
    decayTimer = setInterval(() => {
      if (directionsSwitched > 0) {
        directionsSwitched = Math.max(0, directionsSwitched - 0.7);
        updateGauge(directionsSwitched);
      }
    }, 100);
  });

  document.addEventListener('mousemove', (e) => {
    if (!isTrackingShake) return;
    
    const currentX = e.clientX;
    const deltaX = currentX - lastMouseX;
    
    if (Math.abs(deltaX) > 3) { // threshold movement scale (reduced from 8 for easy shake)
      const currentDirection = deltaX > 0 ? 1 : -1;
      
      if (lastDirection !== 0 && currentDirection !== lastDirection) {
        // Reversal of shake vector direction detected!
        directionsSwitched++;
        updateGauge(directionsSwitched);
        
        // Summon on achieving 3 rapid direction reversals (reduced from 8 for ultra-easy unlock)
        if (directionsSwitched >= 3) {
          triggerSummoSummon();
        }
      }
      
      lastDirection = currentDirection;
      lastMouseX = currentX;
    }
  });

  document.addEventListener('mouseup', () => {
    if (!isTrackingShake) return;
    isTrackingShake = false;
    gestureSandbox.classList.remove('shaking');
    if (decayTimer) clearInterval(decayTimer);
    
    // Reset gauge slowly if not unlocked
    if (!winSumoOverlay.classList.contains('unlocked')) {
      directionsSwitched = 0;
      updateGauge(0);
    }
  });

  // --- MOBILE TOUCH SWIPE SHAKE & TAP FALLBACK ---
  gestureSandbox.addEventListener('touchstart', (e) => {
    if (winSumoOverlay.classList.contains('unlocked') && e.target.closest('#win-sumo-overlay')) {
      return;
    }
    
    // Prevent default scroll/zoom behaviors during touch gestures
    e.preventDefault();
    
    isTrackingShake = true;
    shakeAccumulator = 0;
    directionsSwitched = 0;
    lastMouseX = e.touches[0].clientX;
    lastDirection = 0;
    gestureSandbox.classList.add('shaking');
    
    // Decay gauge over time
    if (decayTimer) clearInterval(decayTimer);
    decayTimer = setInterval(() => {
      if (directionsSwitched > 0) {
        directionsSwitched = Math.max(0, directionsSwitched - 0.7);
        updateGauge(directionsSwitched);
      }
    }, 100);
  }, { passive: false });

  gestureSandbox.addEventListener('touchmove', (e) => {
    if (!isTrackingShake) return;
    
    const currentX = e.touches[0].clientX;
    const deltaX = currentX - lastMouseX;
    
    if (Math.abs(deltaX) > 3) {
      const currentDirection = deltaX > 0 ? 1 : -1;
      
      if (lastDirection !== 0 && currentDirection !== lastDirection) {
        directionsSwitched++;
        updateGauge(directionsSwitched);
        
        if (directionsSwitched >= 3) {
          triggerSummoSummon();
        }
      }
      
      lastDirection = currentDirection;
      lastMouseX = currentX;
    }
  }, { passive: true });

  gestureSandbox.addEventListener('touchend', () => {
    if (!isTrackingShake) return;
    isTrackingShake = false;
    gestureSandbox.classList.remove('shaking');
    if (decayTimer) clearInterval(decayTimer);
    
    if (!winSumoOverlay.classList.contains('unlocked')) {
      directionsSwitched = 0;
      updateGauge(0);
    }
  });

  // Mobile Tap Fallback: rapid clicking/tapping inside the sandbox sums up the gauge
  let lastTapTime = 0;
  let tapCount = 0;
  gestureSandbox.addEventListener('click', (e) => {
    if (winSumoOverlay.classList.contains('unlocked')) return;
    
    // Check if touch is supported or mobile width
    const isMobileViewport = window.innerWidth <= 768;
    if (!isMobileViewport && !('ontouchstart' in window)) return;
    
    const now = Date.now();
    if (now - lastTapTime < 400) {
      tapCount++;
      directionsSwitched = Math.min(3, tapCount);
      updateGauge(directionsSwitched);
      
      if (directionsSwitched >= 3) {
        triggerSummoSummon();
        tapCount = 0;
      }
    } else {
      tapCount = 1;
      directionsSwitched = 1;
      updateGauge(directionsSwitched);
    }
    lastTapTime = now;
  });

  let isGaugeUpdatePending = false;
  let currentScoreValue = 0;

  function updateGauge(score) {
    currentScoreValue = score;
    if (isGaugeUpdatePending) return;
    
    isGaugeUpdatePending = true;
    requestAnimationFrame(() => {
      const percentage = Math.min((currentScoreValue / 3) * 100, 100);
      shakeGauge.style.width = `${percentage}%`;
      isGaugeUpdatePending = false;
    });
  }

  function triggerSummoSummon() {
    isTrackingShake = false;
    gestureSandbox.classList.remove('shaking');
    if (decayTimer) clearInterval(decayTimer);
    
    gestureInstructionBox.style.opacity = '0';
    winSumoOverlay.classList.add('unlocked');
  }

  btnCloseSumo.addEventListener('click', (e) => {
    e.stopPropagation();
    winSumoOverlay.classList.remove('unlocked');
    setTimeout(() => {
      gestureInstructionBox.style.opacity = '1';
      updateGauge(0);
    }, 300);
  });


  /* ==========================================
     4. ADVANCED SMART ACTION: PDF MERGER (VISUALS)
     ========================================== */
  const pdfQueue = document.getElementById('pdf-file-queue');
  const btnTriggerMerge = document.getElementById('btn-trigger-merge');
  const pdfActionBlock = document.getElementById('pdf-action-block');
  const pdfLoadingOverlay = document.getElementById('pdf-merge-loading-overlay');
  const pdfLiveProgressMsg = document.getElementById('pdf-live-progress-msg');
  const pdfOutputSuccessCard = document.getElementById('pdf-output-success-card');
  const btnResetPdfMerger = document.getElementById('btn-reset-pdf-merger');
  const pdfStatusConsole = document.getElementById('pdf-status-console');

  // Move queue items UP or DOWN in queue
  pdfQueue.addEventListener('click', (e) => {
    const btn = e.target.closest('.pdf-ctrl-btn');
    if (!btn) return;
    
    const item = btn.closest('.pdf-item');
    if (!item) return;
    
    if (btn.classList.contains('move-up')) {
      const prev = item.previousElementSibling;
      if (prev) {
        pdfQueue.insertBefore(item, prev);
      }
    } else if (btn.classList.contains('move-down')) {
      const next = item.nextElementSibling;
      if (next) {
        pdfQueue.insertBefore(next, item);
      }
    }
    
    pdfStatusConsole.textContent = "Sequence reorganized...";
  });

  // Stitch files trigger click
  btnTriggerMerge.addEventListener('click', () => {
    pdfQueue.style.display = 'none';
    pdfActionBlock.style.display = 'none';
    pdfLoadingOverlay.style.display = 'flex';
    
    const messages = [
      "Stitching document buffers...",
      "Reading catalog properties...",
      "Compiling high-DPI compressed stream...",
      "Finishing PDF assembly..."
    ];
    
    let msgIndex = 0;
    const progressInterval = setInterval(() => {
      if (msgIndex < messages.length - 1) {
        msgIndex++;
        pdfLiveProgressMsg.textContent = messages[msgIndex];
      }
    }, 850);
    
    setTimeout(() => {
      clearInterval(progressInterval);
      pdfLoadingOverlay.style.display = 'none';
      pdfOutputSuccessCard.style.display = 'flex';
    }, 3400);
  });

  // Stitch Again click reset
  btnResetPdfMerger.addEventListener('click', () => {
    pdfOutputSuccessCard.style.display = 'none';
    pdfQueue.style.display = 'flex';
    pdfActionBlock.style.display = 'flex';
    pdfStatusConsole.textContent = "Ready to stitch...";
  });


  /* ==========================================
     5. ADVANCED SMART ACTION: COUNTDOWN TIMER
     ========================================== */
  const timerCmdInput = document.getElementById('timer-cmd-input');
  const btnTimerRun = document.getElementById('btn-timer-run');
  const timerProgressRing = document.getElementById('timer-progress-ring');
  const timerCountLabel = document.getElementById('timer-count-label');
  
  let timerInterval = null;
  const ringCircumference = 339.29; // 2 * pi * 54 (radius)

  // Configure initial state
  timerProgressRing.style.strokeDasharray = ringCircumference;
  timerProgressRing.style.strokeDashoffset = ringCircumference;

  btnTimerRun.addEventListener('click', () => {
    if (timerInterval) clearInterval(timerInterval);
    
    let command = timerCmdInput.value.trim().toLowerCase();
    
    // Parse duration input parameters
    let seconds = 10; // default fallback
    
    if (command.startsWith('/')) {
      command = command.slice(1);
    }
    
    if (command.endsWith('s')) {
      seconds = parseInt(command) || 10;
    } else if (command.endsWith('m') || command.endsWith('min')) {
      seconds = (parseInt(command) || 1) * 60;
    } else {
      seconds = parseInt(command) || 10;
    }
    
    runCountdown(seconds);
  });

  function runCountdown(totalSeconds) {
    let remainingSeconds = totalSeconds;
    
    updateTimerVisuals(remainingSeconds, totalSeconds);
    
    timerInterval = setInterval(() => {
      remainingSeconds--;
      updateTimerVisuals(remainingSeconds, totalSeconds);
      
      if (remainingSeconds <= 0) {
        clearInterval(timerInterval);
        timerCountLabel.textContent = "Done!";
        timerProgressRing.style.stroke = 'var(--color-emerald)';
        timerProgressRing.style.strokeDashoffset = 0;
        
        // brief pulse animation upon completion
        timerProgressRing.style.animation = 'pulse-ring 1s';
        setTimeout(() => {
          timerProgressRing.style.animation = '';
        }, 1000);
      }
    }, 1000);
  }

  function updateTimerVisuals(remaining, total) {
    timerCountLabel.textContent = `${remaining}s`;
    
    // SVG circular stroke calculation offsets
    const progressFraction = remaining / total;
    const strokeOffset = ringCircumference * (1 - progressFraction);
    
    timerProgressRing.style.strokeDashoffset = strokeOffset;
    
    // Dynamic ring color transitions based on duration percentages
    if (progressFraction > 0.5) {
      timerProgressRing.style.stroke = 'var(--color-cyan)';
    } else if (progressFraction > 0.25) {
      timerProgressRing.style.stroke = 'var(--color-amber)';
    } else {
      timerProgressRing.style.stroke = 'var(--color-red)';
    }
  }


  /* ==========================================
     6. ADVANCED SMART ACTION: COLOR PREVIEWER
     ========================================== */
  const colorCodeField = document.getElementById('color-code-field');
  const liveColorSwatch = document.getElementById('live-color-swatch');
  
  const btnCopyHex = document.getElementById('btn-copy-hex');
  const btnCopyRgb = document.getElementById('btn-copy-rgb');
  const btnCopyHsl = document.getElementById('btn-copy-hsl');

  colorCodeField.addEventListener('input', () => {
    const value = colorCodeField.value.trim();
    
    // Simple verification check to support HEX, RGB, HSL strings
    if (value.startsWith('#') || value.startsWith('rgb') || value.startsWith('hsl')) {
      liveColorSwatch.style.backgroundColor = value;
      liveColorSwatch.style.boxShadow = `0 8px 24px ${value}`;
    }
  });

  // Mock clipboard actions triggers
  btnCopyHex.addEventListener('click', () => {
    navigator.clipboard.writeText(colorCodeField.value);
    alertCopy(colorCodeField.value);
  });

  btnCopyRgb.addEventListener('click', () => {
    // Basic mock RGB convert calculations
    const rgbMockVal = "rgb(0, 210, 255)";
    navigator.clipboard.writeText(rgbMockVal);
    alertCopy(rgbMockVal);
  });

  btnCopyHsl.addEventListener('click', () => {
    const hslMockVal = "hsl(190, 100%, 50%)";
    navigator.clipboard.writeText(hslMockVal);
    alertCopy(hslMockVal);
  });

  function alertCopy(text) {
    const tempAlert = document.createElement('div');
    tempAlert.className = 'badge badge-cyan';
    tempAlert.style.position = 'fixed';
    tempAlert.style.bottom = '30px';
    tempAlert.style.left = '50%';
    tempAlert.style.transform = 'translateX(-50%)';
    tempAlert.style.zIndex = '9999';
    tempAlert.textContent = `Copied to Clipboard: ${text}`;
    document.body.appendChild(tempAlert);
    
    setTimeout(() => {
      tempAlert.remove();
    }, 2000);
  }


  /* ==========================================
     7. ADVANCED SMART ACTION: UTM SANITIZER
     ========================================== */
  const utmUrlInput = document.getElementById('utm-url-input');
  const btnUtmScrubTrigger = document.getElementById('btn-utm-scrub-trigger');

  btnUtmScrubTrigger.addEventListener('click', () => {
    const urlString = utmUrlInput.value.trim();
    
    try {
      const parsedUrl = new URL(urlString);
      const searchParams = parsedUrl.searchParams;
      
      // Selectively purge marketing track queries
      const trackingParams = ['utm_source', 'utm_medium', 'utm_campaign', 'utm_term', 'utm_content', 'fbclid', 'gclid'];
      
      let scrubbedCount = 0;
      trackingParams.forEach(param => {
        if (searchParams.has(param)) {
          searchParams.delete(param);
          scrubbedCount++;
        }
      });
      
      if (scrubbedCount > 0) {
        const cleanedUrl = parsedUrl.toString();
        
        // Highlight area during scrub
        utmUrlInput.classList.add('cleaned');
        utmUrlInput.value = cleanedUrl;
        
        btnUtmScrubTrigger.textContent = "Sanitized!";
        btnUtmScrubTrigger.style.background = 'var(--color-emerald)';
        
        setTimeout(() => {
          utmUrlInput.classList.remove('cleaned');
          btnUtmScrubTrigger.textContent = "Sanitize Link";
          btnUtmScrubTrigger.style.background = 'var(--color-emerald)';
        }, 2200);
      } else {
        alertCopy("No UTM tracking parameters detected.");
      }
    } catch(err) {
      alertCopy("Invalid URL format detected.");
    }
  });


  /* ==========================================
     8. ADVANCED SMART ACTION: TEXT SNIPPETS SHORTS
     ========================================== */
  const snippetShortcutInput = document.getElementById('snippet-shortcut-input');
  const btnExpandSnippet = document.getElementById('btn-expand-snippet');
  const snippetExpandedOutput = document.getElementById('snippet-expanded-output');
  const snippetQuickTags = document.querySelectorAll('[data-shortcut]');

  // Registered Snippets Library
  const snippetsLibrary = {
    '/address': '1600 Amphitheatre Pkwy, Mountain View, CA 94043',
    '/email': 'support@flyshelf.app',
    '/sig': 'Best regards,\nShivendra Prasad\nLead Developer, FlyShelf'
  };

  // Set initial expansion
  if (snippetShortcutInput) {
    expandActiveSnippet();

    // Expand click handler
    btnExpandSnippet.addEventListener('click', () => {
      expandActiveSnippet();
    });

    // Input keypress / change
    snippetShortcutInput.addEventListener('input', () => {
      expandActiveSnippet();
    });

    // Quick Tags click
    snippetQuickTags.forEach(tag => {
      tag.addEventListener('click', () => {
        const shortcut = tag.dataset.shortcut;
        snippetShortcutInput.value = shortcut;
        expandActiveSnippet();
        
        // Flash animation on tag
        tag.style.transform = 'scale(0.95)';
        setTimeout(() => { tag.style.transform = ''; }, 100);
      });
    });
  }

  function expandActiveSnippet() {
    const inputVal = snippetShortcutInput.value.trim().toLowerCase();
    
    if (snippetsLibrary.hasOwnProperty(inputVal)) {
      const expanded = snippetsLibrary[inputVal];
      snippetExpandedOutput.value = expanded;
      snippetExpandedOutput.style.color = 'var(--color-cyan)';
      snippetExpandedOutput.style.borderColor = 'rgba(0, 210, 255, 0.4)';
      snippetExpandedOutput.style.boxShadow = '0 0 15px rgba(0, 210, 255, 0.1)';
    } else {
      snippetExpandedOutput.value = `Shortcut "${inputVal}" is not registered.\nRegistered: /address, /email, /sig`;
      snippetExpandedOutput.style.color = 'var(--text-muted)';
      snippetExpandedOutput.style.borderColor = 'rgba(255, 255, 255, 0.05)';
      snippetExpandedOutput.style.boxShadow = 'none';
    }
  }

});

