/* FlyShelf: a tiny, framework-free playground. All sample data stays in memory. */
(() => {
  'use strict';
  const $ = (selector) => document.querySelector(selector);
  const $$ = (selector) => [...document.querySelectorAll(selector)];
  const reduced = matchMedia('(prefers-reduced-motion: reduce)');
  const ticker = $('.ticker-track');
  if (ticker) {
    // Two equal groups make the endless ribbon loop without a visible jump.
    const items = [...ticker.children];
    const halfway = items.length / 2;
    for (const half of [items.slice(0, halfway), items.slice(halfway)]) {
      const group = document.createElement('div');
      group.className = 'ticker-group';
      group.append(...half);
      ticker.append(group);
    }
  }
  let toastTimer;
  function toast(message) {
    const element = $('#demo-toast');
    if (!element) return;
    clearTimeout(toastTimer);
    element.textContent = message;
    element.classList.add('show');
    toastTimer = setTimeout(() => element.classList.remove('show'), 2800);
  }

  const toggle = $('.menu-toggle');
  const nav = $('#site-nav');
  function closeMenu() {
    nav?.classList.remove('open');
    toggle?.setAttribute('aria-expanded', 'false');
    toggle?.setAttribute('aria-label', 'Open navigation');
  }
  toggle?.addEventListener('click', () => {
    const open = nav.classList.toggle('open');
    toggle.setAttribute('aria-expanded', String(open));
    toggle.setAttribute('aria-label', open ? 'Close navigation' : 'Open navigation');
  });
  $$('#site-nav a').forEach(link => link.addEventListener('click', closeMenu));
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && nav?.classList.contains('open')) {
      closeMenu();
      toggle.focus();
    }
  });
  document.addEventListener('click', event => {
    if (!event.target.closest('.site-header')) closeMenu();
  });
  matchMedia('(min-width: 601px)').addEventListener('change', closeMenu);

  if ('IntersectionObserver' in window && !reduced.matches) {
    const reveals = new IntersectionObserver(entries => {
      for (const entry of entries) if (entry.isIntersecting) {
        entry.target.classList.add('visible');
        reveals.unobserve(entry.target);
      }
    }, { threshold: 0.06, rootMargin: '0px 0px 35px 0px' });
    $$('.reveal').forEach(element => reveals.observe(element));
    document.body.classList.add('motion-ready');
    const art = $('.hero-art');
    const artObserver = new IntersectionObserver(([entry]) => {
      art.classList.toggle('is-paused', !entry.isIntersecting);
    });
    if (art) artObserver.observe(art);
  }
  let scrollQueued = false;
  function updateProgress() {
    const available = document.documentElement.scrollHeight - innerHeight;
    $('.reading-progress')?.style.setProperty('transform', `scaleX(${available > 0 ? scrollY / available : 0})`);
    scrollQueued = false;
  }
  window.addEventListener('scroll', () => {
    if (!scrollQueued) { scrollQueued = true; requestAnimationFrame(updateProgress); }
  }, { passive: true });
  window.addEventListener('resize', updateProgress, { passive: true });
  updateProgress();
  const hero = $('.hero');
  const art = $('.hero-art');
  hero?.addEventListener('pointermove', event => {
    if (event.pointerType !== 'mouse' || reduced.matches || innerWidth < 801 || document.body.classList.contains('motion-paused')) return;
    const bounds = hero.getBoundingClientRect();
    art.style.setProperty('--pointer-x', `${(event.clientX - bounds.left - bounds.width / 2) * .012}px`);
    art.style.setProperty('--pointer-y', `${(event.clientY - bounds.top - bounds.height / 2) * .018}px`);
  }, { passive: true });
  hero?.addEventListener('pointerleave', () => {
    art.style.setProperty('--pointer-x', '0px');
    art.style.setProperty('--pointer-y', '0px');
  });
  document.addEventListener('visibilitychange', () => {
    document.body.classList.toggle('page-hidden', document.hidden);
  });
  const motionButton = document.createElement('button');
  motionButton.className = 'motion-toggle';
  motionButton.textContent = 'Pause motion';
  motionButton.setAttribute('aria-pressed', 'false');
  motionButton.addEventListener('click', () => {
    const paused = document.body.classList.toggle('motion-paused');
    motionButton.textContent = paused ? 'Resume motion' : 'Pause motion';
    motionButton.setAttribute('aria-pressed', String(paused));
  });
  $('.footer-bottom > div')?.append(motionButton);

  const initialClips = [
    { id: 1, type: 'text', label: 'A little reminder', content: 'Make something worth copying.', time: 'Just now', pinned: true },
    { id: 2, type: 'code', label: 'That perfect snippet', content: 'const ideas = capture();\nawait ideas.sync();', time: '2 minutes ago', pinned: false },
    { id: 3, type: 'link', label: 'Some inspiration', content: 'https://www.are.na/', time: '5 minutes ago', pinned: false },
    { id: 4, type: 'text', label: 'The next big thing', content: 'Less friction. More flow.\nKeep the good stuff.', time: '8 minutes ago', pinned: true },
    { id: 5, type: 'link', label: 'Your creative toolkit', content: 'https://github.com/shdra06/FlyShelf', time: '12 minutes ago', pinned: false },
    { id: 6, type: 'code', label: 'A fresh start', content: 'function create() {\n  return somethingGood;\n}', time: '15 minutes ago', pinned: false },
  ];
  let clips = initialClips.map(clip => ({ ...clip }));
  let filter = 'all';
  const search = $('#clip-search');
  function element(tag, className, content) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (content !== undefined) node.textContent = content;
    return node;
  }
  function renderClips() {
    const grid = $('#demo-clips');
    if (!grid) return;
    const query = (search?.value || '').toLowerCase().trim();
    const visible = clips.filter(clip => (filter === 'all' || clip.type === filter || (filter === 'pinned' && clip.pinned)) && `${clip.label} ${clip.content}`.toLowerCase().includes(query));
    grid.replaceChildren();
    for (const clip of visible) {
      const card = element('article', 'demo-clip');
      card.dataset.type = clip.type;
      const top = element('div', 'demo-clip-top');
      const symbol = clip.type === 'code' ? '</>' : clip.type === 'link' ? '↗' : 'Aa';
      top.append(element('span', `mini-symbol ${clip.type === 'code' ? 'violet' : clip.type === 'link' ? 'mint' : 'peach'}`, symbol), element('span', '', clip.label));
      const pin = element('button', 'demo-pin', clip.pinned ? '◆' : '◇');
      pin.dataset.action = 'pin'; pin.dataset.id = clip.id;
      pin.setAttribute('aria-label', `Pin ${clip.label}`);
      pin.setAttribute('aria-pressed', String(clip.pinned));
      top.append(pin);
      card.append(top, element('p', 'demo-clip-content', clip.content));
      const bottom = element('div', 'demo-clip-bottom');
      const copy = element('button', 'demo-copy', 'Copy ↗');
      copy.dataset.action = 'copy'; copy.dataset.id = clip.id;
      copy.setAttribute('aria-label', `Copy ${clip.label}`);
      bottom.append(element('span', '', clip.time), copy);
      card.append(bottom);
      grid.append(card);
    }
    $('#demo-empty').hidden = visible.length > 0;
  }
  $$('.demo-filters button').forEach(button => {
    button.addEventListener('click', () => {
      filter = button.dataset.filter;
      $$('.demo-filters button').forEach(candidate => {
        candidate.classList.toggle('active', candidate === button);
        candidate.setAttribute('aria-pressed', String(candidate === button));
      });
      renderClips();
    });
  });
  search?.addEventListener('input', renderClips);
  $('#demo-clips')?.addEventListener('click', async event => {
    const button = event.target.closest('button[data-action]');
    if (!button) return;
    const clip = clips.find(candidate => candidate.id === Number(button.dataset.id));
    if (!clip) return;
    if (button.dataset.action === 'pin') {
      clip.pinned = !clip.pinned;
      // Update in place to preserve keyboard focus, even in the pinned filter.
      button.textContent = clip.pinned ? '◆' : '◇';
      button.setAttribute('aria-pressed', String(clip.pinned));
      if (filter === 'pinned' && !clip.pinned) {
        renderClips();
        ($('#demo-clips .demo-pin') || $('.demo-filters [data-filter="pinned"]')).focus();
      }
      toast(clip.pinned ? 'Pinned. A keeper.' : 'Unpinned from your sample shelf.');
    } else {
      try {
        await navigator.clipboard.writeText(clip.content);
        toast('Copied. Go make something good.');
      } catch { toast('Clipboard access is unavailable. Select the sample text to copy it.'); }
    }
  });
  $('#demo-reset')?.addEventListener('click', () => {
    clips = initialClips.map(clip => ({ ...clip }));
    search.value = '';
    $('.demo-filters [data-filter="all"]').click();
    toast('A fresh shelf. All yours.');
  });
  document.addEventListener('keydown', event => {
    if (event.key !== '/' || event.ctrlKey || event.metaKey || event.altKey || /INPUT|TEXTAREA|SELECT/.test(event.target.tagName) || event.target.isContentEditable) return;
    event.preventDefault();
    search?.scrollIntoView({ block: 'center', behavior: reduced.matches ? 'instant' : 'smooth' });
    search?.focus({ preventScroll: true });
  });
  renderClips();

  $('#sync-trigger')?.addEventListener('click', () => {
    const button = $('#sync-trigger');
    const stage = $('.sync-stage');
    button.disabled = true;
    stage.classList.remove('synced');
    stage.classList.add('syncing');
    $('#sync-state').textContent = 'Your idea is on its way…';
    setTimeout(() => {
      $('#sync-target').textContent = $('#sync-source').textContent;
      $('#sync-state').textContent = 'Same idea. Another device.';
      stage.classList.remove('syncing');
      stage.classList.add('synced');
      button.disabled = false;
      button.textContent = 'Play it again ↻';
    }, reduced.matches ? 0 : 1700);
  });

  const shots = {
    clipboard: { src: 'assets/clipboard.png', width: 454, height: 477, caption: 'Everything you copied. Beautifully within reach.', alt: 'FlyShelf floating clipboard with file filters, text and image clips' },
    tools: { src: 'assets/pdf-studio.png', width: 1916, height: 1015, caption: 'Meet PDF Studio. Your documents, with possibilities.', alt: 'FlyShelf PDF Studio with merge, split, rotate, compress and export tools' },
    notes: { src: 'assets/notes.png', width: 461, height: 483, caption: 'A small place to put the thought you don’t want to lose.', alt: 'FlyShelf floating notes panel with daily notes and to-do list access' },
    themes: { src: 'assets/personalization.png', width: 1141, height: 832, caption: 'Your wallpaper. Your colors. Your kind of workspace.', alt: 'FlyShelf personalization panel with wallpaper, blur and appearance settings' },
  };
  let shotRequest = 0;
  async function selectShot(button) {
    const shot = shots[button.dataset.shot];
    const request = ++shotRequest;
    const preloaded = new Image();
    preloaded.src = shot.src;
    try { await preloaded.decode(); } catch { toast('This app image couldn’t load. Please try again.'); return; }
    if (request !== shotRequest) return;
    $$('.showcase-tabs button').forEach(candidate => {
      const selected = candidate === button;
      candidate.setAttribute('aria-selected', String(selected));
      candidate.tabIndex = selected ? 0 : -1;
    });
    const image = $('#app-screenshot');
    image.src = shot.src; image.alt = shot.alt; image.width = shot.width; image.height = shot.height;
    $('#shot-caption').textContent = shot.caption;
    const panel = $('#app-shot-panel');
    panel.className = `screenshot-stage reveal visible shot-${button.dataset.shot}`;
    panel.setAttribute('aria-labelledby', button.id);
    const surface = $('.shot-surface');
    surface.classList.remove('changing');
    requestAnimationFrame(() => surface.classList.add('changing'));
  }
  const tabs = $$('.showcase-tabs button');
  tabs.forEach((button, index) => {
    button.addEventListener('click', () => selectShot(button));
    button.addEventListener('keydown', event => {
      const next = event.key === 'ArrowRight' ? (index + 1) % tabs.length : event.key === 'ArrowLeft' ? (index + tabs.length - 1) % tabs.length : event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 : null;
      if (next === null) return;
      event.preventDefault(); tabs[next].focus(); selectShot(tabs[next]);
    });
  });
  let vibe = 0;
  $('#theme-shift')?.addEventListener('click', () => {
    vibe = (vibe + 1) % 3;
    document.body.classList.toggle('vibe-lavender', vibe === 1);
    document.body.classList.toggle('vibe-peach', vibe === 2);
    toast(['Electric mint. Fresh perspective.', 'Lavender. A little dreamier.', 'Peach. A warmer kind of flow.'][vibe]);
  });
  $('#drag-demo')?.addEventListener('click', () => {
    const button = $('#drag-demo');
    const scene = $('.drag-scene');
    scene.classList.remove('delivered');
    scene.classList.add('moving');
    button.disabled = true;
    $('#drag-status').textContent = 'From your shelf to your next project…';
    setTimeout(() => {
      scene.classList.remove('moving'); scene.classList.add('delivered');
      $('.drop-target p').textContent = 'Right where you need it.';
      $('#drag-status').textContent = 'Dropped. Back to your flow. (Illustrated demo)';
      button.disabled = false;
      button.textContent = 'Once more ↻';
    }, reduced.matches ? 0 : 1700);
  });
  if (/Android/i.test(navigator.userAgent)) {
    const android = $('#download-android');
    const windows = $('#download-windows');
    android?.classList.replace('secondary', 'primary');
    windows?.classList.replace('primary', 'secondary');
    if (android && windows) android.parentNode.insertBefore(android, windows);
  }
  if ($('#copyright-year')) $('#copyright-year').textContent = new Date().getFullYear();
})();
