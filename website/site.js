/* Shared page chrome: navigation, reveals, motion control and toasts. No dependencies. */
(() => {
  'use strict';
  const $ = (selector, scope = document) => scope.querySelector(selector);
  const $$ = (selector, scope = document) => [...scope.querySelectorAll(selector)];
  const root = document.documentElement;
  const reduced = matchMedia('(prefers-reduced-motion: reduce)');
  const motionKey = 'flyshelf-site-motion';
  const listeners = new Set();
  const motionAllowed = () => !reduced.matches && !root.classList.contains('motion-paused') && !document.hidden;

  // Public, tiny API for page scripts.
  const site = window.FlyShelfSite = {
    $, $$, motionAllowed,
    onMotionChange(callback) { listeners.add(callback); },
    toast,
    wait: ms => new Promise(resolve => setTimeout(resolve, motionAllowed() ? ms : 0)),
  };
  const notify = () => listeners.forEach(callback => callback(motionAllowed()));

  let toastTimer;
  function toast(message) {
    let element = $('#site-toast') || $('#demo-toast');
    if (!element) {
      element = document.createElement('div');
      element.id = 'site-toast';
      element.className = 'toast';
      element.setAttribute('role', 'status');
      element.setAttribute('aria-live', 'polite');
      document.body.append(element);
    }
    clearTimeout(toastTimer);
    element.textContent = message;
    element.classList.add('show');
    toastTimer = setTimeout(() => element.classList.remove('show'), 3000);
  }

  // Navigation
  const toggle = $('.menu-toggle');
  const nav = $('#site-nav');
  function closeMenu(returnFocus = false) {
    if (!nav?.classList.contains('open')) return;
    nav.classList.remove('open');
    toggle?.setAttribute('aria-expanded', 'false');
    toggle?.setAttribute('aria-label', 'Open navigation');
    if (returnFocus) toggle?.focus();
  }
  toggle?.addEventListener('click', () => {
    const open = nav.classList.toggle('open');
    toggle.setAttribute('aria-expanded', String(open));
    toggle.setAttribute('aria-label', open ? 'Close navigation' : 'Open navigation');
    if (open) $('a', nav)?.focus({ preventScroll: true });
  });
  $$('#site-nav a').forEach(link => link.addEventListener('click', () => closeMenu()));
  document.addEventListener('keydown', event => { if (event.key === 'Escape') closeMenu(true); });
  document.addEventListener('click', event => { if (!event.target.closest('.site-header')) closeMenu(); });
  matchMedia('(min-width: 881px)').addEventListener('change', () => closeMenu());

  // Header state and current section
  const header = $('.site-header');
  const sectionLinks = $$('#site-nav a[href*="#"], .policy-toc a').filter(link => link.hash && document.getElementById(link.hash.slice(1)));
  let queued = false;
  function onScroll() {
    queued = false;
    header?.classList.toggle('is-scrolled', scrollY > 8);
    for (const group of [sectionLinks.filter(link => link.closest('#site-nav')), sectionLinks.filter(link => !link.closest('#site-nav'))]) {
      let current = null;
      for (const link of group) {
        const top = document.getElementById(link.hash.slice(1)).getBoundingClientRect().top;
        if (top <= innerHeight * .35) current = link;
      }
      group.forEach(link => link === current ? link.setAttribute('aria-current', 'location') : link.removeAttribute('aria-current'));
    }
  }
  addEventListener('scroll', () => { if (!queued) { queued = true; requestAnimationFrame(onScroll); } }, { passive: true });
  onScroll();

  // Reveals: only opt in when motion is allowed, so content never waits on script.
  function setupReveals() {
    if (!('IntersectionObserver' in window) || reduced.matches || root.classList.contains('motion-paused')) return;
    const pending = $$('.reveal:not(.visible)');
    if (!pending.length) return;
    const observer = new IntersectionObserver(entries => {
      for (const entry of entries) if (entry.isIntersecting) {
        entry.target.classList.add('visible');
        observer.unobserve(entry.target);
      }
    }, { threshold: .08, rootMargin: '0px 0px -6% 0px' });
    // Anything already on screen shows at once; the rest rises in as it arrives.
    for (const element of pending) {
      if (element.getBoundingClientRect().top < innerHeight * .92) element.classList.add('visible');
      else observer.observe(element);
    }
    root.classList.add('motion-ready');
  }
  setupReveals();

  // Pause decorative work while the tab is hidden.
  document.addEventListener('visibilitychange', () => {
    root.classList.toggle('page-hidden', document.hidden);
    notify();
  });

  // Motion control, persisted per visitor.
  const motionButton = document.createElement('button');
  motionButton.type = 'button';
  motionButton.className = 'motion-toggle';
  function updateMotionControl() {
    const paused = root.classList.contains('motion-paused');
    document.body.classList.toggle('motion-paused', paused);
    motionButton.textContent = reduced.matches ? 'Reduced motion' : paused ? 'Resume motion' : 'Pause motion';
    motionButton.setAttribute('aria-pressed', String(paused || reduced.matches));
    motionButton.disabled = reduced.matches;
    motionButton.title = reduced.matches ? 'Your device is set to reduce motion' : 'Pause or resume decorative animation';
  }
  motionButton.addEventListener('click', () => {
    const paused = root.classList.toggle('motion-paused');
    try { localStorage.setItem(motionKey, paused ? 'paused' : 'playing'); } catch { /* This page still honours the choice. */ }
    updateMotionControl();
    if (paused) $$('.reveal').forEach(element => element.classList.add('visible'));
    notify();
  });
  reduced.addEventListener('change', () => {
    if (reduced.matches) $$('.reveal').forEach(element => element.classList.add('visible'));
    updateMotionControl();
    notify();
  });
  updateMotionControl();
  ($('.footer-controls') || $('.site-footer'))?.append(motionButton);

  const year = $('#copyright-year');
  if (year) year.textContent = new Date().getFullYear();
})();
