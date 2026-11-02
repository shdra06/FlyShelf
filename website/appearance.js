/* Shared appearance, applied in <head> before first paint to avoid a theme or motion flash. */
(() => {
  'use strict';
  const key = 'flyshelf-site-appearance';
  const motionKey = 'flyshelf-site-motion';
  const root = document.documentElement;
  const systemDark = matchMedia('(prefers-color-scheme: dark)');
  const reduced = matchMedia('(prefers-reduced-motion: reduce)');
  const read = name => { try { return localStorage.getItem(name); } catch { return null; } };

  try { if (read(motionKey) === 'paused') root.classList.add('motion-paused'); } catch { /* Motion stays on. */ }

  const icons = {
    moon: '<path d="M20 14.2A8 8 0 0 1 9.8 4a8 8 0 1 0 10.2 10.2Z"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2.5v2M12 19.5v2M2.5 12h2M19.5 12h2M5.3 5.3l1.4 1.4M17.3 17.3l1.4 1.4M5.3 18.7l1.4-1.4M17.3 6.7l1.4-1.4"/>',
  };

  function apply(theme, persist = false) {
    root.dataset.theme = theme === 'dark' ? 'dark' : 'light';
    const dark = root.dataset.theme === 'dark';
    document.querySelector('meta[name="theme-color"]')?.setAttribute('content', dark ? '#0b0d11' : '#f4f3ef');
    document.querySelectorAll('.appearance-toggle').forEach(button => {
      button.setAttribute('aria-label', `Switch to ${dark ? 'light' : 'dark'} theme`);
      button.title = dark ? 'Switch to light theme' : 'Switch to dark theme';
      button.querySelector('.appearance-label').textContent = dark ? 'Light' : 'Dark';
      button.querySelector('.appearance-icon').innerHTML = dark ? icons.sun : icons.moon;
    });
    if (persist) try { localStorage.setItem(key, root.dataset.theme); } catch { /* Theme still works without storage. */ }
  }

  // An explicit choice wins; otherwise follow the device.
  const saved = read(key);
  apply(saved || (systemDark.matches ? 'dark' : 'light'));
  systemDark.addEventListener('change', event => { if (!read(key)) apply(event.matches ? 'dark' : 'light'); });

  function toggle(button) {
    const next = root.dataset.theme === 'light' ? 'dark' : 'light';
    const calm = reduced.matches || root.classList.contains('motion-paused') || !document.startViewTransition;
    if (calm) { apply(next, true); return; }
    const box = button.getBoundingClientRect();
    const x = box.left + box.width / 2;
    const y = box.top + box.height / 2;
    root.style.setProperty('--reveal-x', `${x}px`);
    root.style.setProperty('--reveal-y', `${y}px`);
    root.style.setProperty('--reveal-r', `${Math.hypot(Math.max(x, innerWidth - x), Math.max(y, innerHeight - y))}px`);
    document.startViewTransition(() => apply(next, true));
  }

  document.addEventListener('DOMContentLoaded', () => {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'appearance-toggle';
    button.innerHTML = '<svg class="appearance-icon" viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"></svg><span class="appearance-label"></span>';
    const slot = document.querySelector('.header-actions');
    if (slot) slot.prepend(button);
    button.addEventListener('click', () => toggle(button));
    apply(root.dataset.theme);
  });

  window.addEventListener('storage', event => {
    if (event.key === key) apply(event.newValue || (systemDark.matches ? 'dark' : 'light'));
  });
})();
