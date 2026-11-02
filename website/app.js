/* FlyShelf homepage: illustrations and the sample shelf. All sample data stays in memory. */
(() => {
  'use strict';
  const site = window.FlyShelfSite;
  if (!site) return;
  const { $, $$, motionAllowed, toast, wait } = site;
  const ease = 'cubic-bezier(.22,1,.36,1)';
  const svgNS = 'http://www.w3.org/2000/svg';

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }
  function icon(name, className) {
    const svg = document.createElementNS(svgNS, 'svg');
    svg.setAttribute('aria-hidden', 'true');
    if (className) svg.setAttribute('class', className);
    const use = document.createElementNS(svgNS, 'use');
    use.setAttribute('href', `#i-${name}`);
    svg.append(use);
    return svg;
  }

  /* ── Hero: copies fly onto the shelf ─────────────────────────────────── */
  const stage = $('.stage');
  const heroList = $('#hero-list');
  const flyLayer = $('#hero-fly-layer');
  const phoneCard = $('#hero-phone-card');
  const heroCopies = [
    { type: 'text', from: 'Mail', text: 'Pickup at 7:15 · Gate B, level 2', sub: 'Mail — Travel plans', send: true },
    { type: 'code', from: 'Terminal', text: 'git commit -m "Polish onboarding"', sub: 'Windows Terminal' },
    { type: 'link', from: 'Browser', text: 'developer.mozilla.org/docs/Web/CSS', sub: 'Browser' },
    { type: 'file', from: 'Explorer', text: 'quarterly-report.xlsx', sub: 'Explorer · 82 KB' },
    { type: 'text', from: 'Notepad', text: 'Send the final draft by Friday', sub: 'Notepad — weekly-plan.txt' },
  ];
  let heroIndex = 0;
  let heroTimer = 0;
  let heroVisible = false;
  let heroBusy = false;

  function heroCard(copy) {
    const card = element('li', `app-card card-${copy.type}`);
    if (copy.type === 'file') card.append(icon('pdf', 'pdf-glyph'));
    else if (copy.type !== 'text') {
      const glyph = element('span', `card-glyph ${copy.type}`);
      glyph.append(icon(copy.type === 'code' ? 'code' : 'link'));
      card.append(glyph);
    }
    const main = element('span', 'card-main', copy.text);
    main.append(element('small', '', copy.sub));
    card.append(main);
    return card;
  }
  function relative(rect) {
    const box = stage.getBoundingClientRect();
    return { x: rect.left - box.left, y: rect.top - box.top, w: rect.width, h: rect.height };
  }
  async function flyNext() {
    if (heroBusy || !heroVisible || !motionAllowed()) return;
    heroBusy = true;
    const copy = heroCopies[heroIndex++ % heroCopies.length];
    const chip = element('div', 'fly-chip');
    const meta = element('span', 'fly-meta');
    const keys = element('span');
    keys.append(element('kbd', '', 'Ctrl'), ' ', element('kbd', '', 'C'));
    meta.append(element('span', '', `Copied in ${copy.from}`), keys);
    chip.append(meta, element('span', 'fly-text', copy.text));
    flyLayer.append(chip);

    const box = stage.getBoundingClientRect();
    const target = relative(heroList.getBoundingClientRect());
    const start = { x: -box.width * .04, y: box.height * .86 };
    const end = { x: target.x, y: target.y };
    const mid = { x: start.x + (end.x - start.x) * .35, y: Math.min(start.y, end.y) - box.height * .02 };
    const scale = Math.min(1.12, target.w / chip.offsetWidth);
    const flight = chip.animate([
      { transform: `translate(${start.x}px, ${start.y}px) scale(.92)`, opacity: 0 },
      { transform: `translate(${start.x}px, ${start.y - 14}px) scale(1)`, opacity: 1, offset: .16 },
      { transform: `translate(${mid.x}px, ${mid.y}px) scale(1.02)`, opacity: 1, offset: .58 },
      { transform: `translate(${end.x}px, ${end.y}px) scale(${scale}, 1)`, opacity: .0 },
    ], { duration: 1250, easing: 'cubic-bezier(.45,.05,.25,1)', fill: 'forwards' });
    await flight.finished.catch(() => {});
    chip.remove();

    // FLIP the list so existing clips glide down to make room.
    const before = new Map([...heroList.children].map(card => [card, card.getBoundingClientRect().top]));
    const card = heroCard(copy);
    heroList.prepend(card);
    for (const existing of heroList.children) {
      const previous = before.get(existing);
      if (previous === undefined) continue;
      const delta = previous - existing.getBoundingClientRect().top;
      if (delta) existing.animate([{ transform: `translateY(${delta}px)` }, { transform: 'none' }], { duration: 640, easing: ease });
    }
    card.animate([{ opacity: 0, transform: 'scale(.96)', filter: 'brightness(1.6)' }, { opacity: 1, transform: 'none', filter: 'none' }], { duration: 560, easing: ease });
    while (heroList.children.length > 6) heroList.lastElementChild.remove();

    if (copy.send) await sendToPhone(card, copy);
    heroBusy = false;
  }
  async function sendToPhone(card, copy) {
    await new Promise(resolve => setTimeout(resolve, 700));
    if (!motionAllowed()) return;
    const from = relative(card.getBoundingClientRect());
    const to = relative(phoneCard.getBoundingClientRect());
    const packet = element('span', 'fly-packet');
    packet.append(icon('phone'));
    flyLayer.append(packet);
    const size = packet.offsetWidth;
    const sx = from.x + from.w - size * 1.4;
    const sy = from.y + (from.h - size) / 2;
    const ex = to.x + (to.w - size) / 2;
    const ey = to.y + (to.h - size) / 2;
    await packet.animate([
      { transform: `translate(${sx}px, ${sy}px) scale(.4)`, opacity: 0 },
      { transform: `translate(${sx}px, ${sy}px) scale(1)`, opacity: 1, offset: .18 },
      { transform: `translate(${(sx + ex) / 2}px, ${Math.min(sy, ey) - 30}px) scale(1)`, opacity: 1, offset: .55 },
      { transform: `translate(${ex}px, ${ey}px) scale(.6)`, opacity: 0 },
    ], { duration: 1100, easing: 'cubic-bezier(.45,.05,.25,1)', fill: 'forwards' }).finished.catch(() => {});
    packet.remove();
    phoneCard.firstElementChild.textContent = copy.text;
    phoneCard.classList.remove('arrived');
    void phoneCard.offsetWidth;
    phoneCard.classList.add('arrived');
  }
  function scheduleHero(delay = 3300) {
    clearTimeout(heroTimer);
    if (!heroVisible || !motionAllowed()) return;
    heroTimer = setTimeout(async () => { await flyNext(); scheduleHero(); }, delay);
  }
  if (stage && heroList && 'IntersectionObserver' in window && Element.prototype.animate) {
    new IntersectionObserver(([entry]) => {
      heroVisible = entry.isIntersecting;
      if (heroVisible) scheduleHero(heroIndex ? 1400 : 1600);
      else clearTimeout(heroTimer);
    }, { threshold: .25 }).observe(stage);
    site.onMotionChange(allowed => allowed ? scheduleHero(900) : clearTimeout(heroTimer));
  }

  /* ── Sample shelf ────────────────────────────────────────────────────── */
  const initialClips = [
    { id: 1, type: 'text', label: 'Note to self', content: 'Call the venue before 3 pm — ask about the projector.', time: 'Just now', pinned: true },
    { id: 2, type: 'code', label: 'Deploy command', content: 'git push origin main && npm run deploy', time: '2 min ago', pinned: false },
    { id: 3, type: 'link', label: 'CSS reference', content: 'https://developer.mozilla.org/docs/Web/CSS', time: '6 min ago', pinned: false },
    { id: 4, type: 'text', label: 'Quarter goals', content: 'Launch the beta. Hire a designer. Publish the docs.', time: '18 min ago', pinned: true },
    { id: 5, type: 'link', label: 'FlyShelf on GitHub', content: 'https://github.com/shdra06/FlyShelf', time: '1 hr ago', pinned: false },
    { id: 6, type: 'code', label: 'Python one-liner', content: 'print(sorted(set(words)))', time: 'Yesterday', pinned: false },
  ];
  let clips = initialClips.map(clip => ({ ...clip }));
  let filter = 'all';
  const search = $('#clip-search');
  const grid = $('#demo-clips');
  const symbols = { text: 'Aa', link: '↗', code: '</>' };

  function highlight(target, text, query) {
    if (!query) { target.textContent = text; return; }
    const lower = text.toLowerCase();
    let from = 0;
    for (let at = lower.indexOf(query); at !== -1; at = lower.indexOf(query, from)) {
      target.append(text.slice(from, at), element('mark', '', text.slice(at, at + query.length)));
      from = at + query.length;
    }
    target.append(text.slice(from));
  }
  // Cards are built once per shelf; filtering only shows and hides them, so a click
  // repaints the cards that change rather than rebuilding the whole grid.
  const cards = new Map();
  let lastQuery = null;
  function buildCards() {
    cards.clear();
    lastQuery = null;
    grid.replaceChildren(...clips.map(clip => {
      const card = element('article', `demo-clip${clip.pinned ? ' is-pinned' : ''}`);
      card.dataset.type = clip.type;
      const top = element('div', 'demo-clip-top');
      const label = element('span', 'label');
      top.append(element('span', `mini-symbol ${clip.type}`, symbols[clip.type]), label);
      const pin = element('button', 'demo-pin');
      pin.type = 'button';
      pin.dataset.action = 'pin';
      pin.dataset.id = clip.id;
      pin.setAttribute('aria-pressed', String(clip.pinned));
      pin.setAttribute('aria-label', `Pin ${clip.label}`);
      pin.append(icon('pin'));
      top.append(pin);
      const content = element('p', 'demo-clip-content');
      const bottom = element('div', 'demo-clip-bottom');
      const copy = element('button', 'demo-copy');
      copy.type = 'button';
      copy.dataset.action = 'copy';
      copy.dataset.id = clip.id;
      copy.setAttribute('aria-label', `Copy ${clip.label}`);
      copy.append(icon('copy'), element('span', '', 'Copy'));
      bottom.append(element('span', '', clip.time), copy);
      card.append(top, content, bottom);
      cards.set(clip.id, { card, label, content });
      return card;
    }));
  }
  function renderClips() {
    if (!grid) return;
    if (!cards.size) buildCards();
    const query = (search?.value || '').toLowerCase().trim();
    const arriving = [];
    let shown = 0;
    for (const clip of clips) {
      const { card, label, content } = cards.get(clip.id);
      const visible = (filter === 'all' || clip.type === filter || (filter === 'pinned' && clip.pinned))
        && `${clip.label} ${clip.content}`.toLowerCase().includes(query);
      if (query !== lastQuery) {
        label.replaceChildren();
        content.replaceChildren();
        highlight(label, clip.label, query);
        highlight(content, clip.content, query);
      }
      if (visible && card.hidden) arriving.push(card);
      card.hidden = !visible;
      if (visible) shown++;
    }
    // Web Animations avoid a forced layout per card; only newly shown cards move.
    if (motionAllowed()) arriving.forEach((card, index) => card.animate(
      [{ opacity: 0, transform: 'translateY(8px) scale(.99)' }, { opacity: 1, transform: 'none' }],
      { duration: 360, delay: index * 30, easing: ease, fill: 'backwards' }));
    lastQuery = query;
    $('#demo-empty').hidden = shown > 0;
    const count = $('#demo-count');
    if (count) count.textContent = shown === clips.length ? `${clips.length} sample clips` : `Showing ${shown} of ${clips.length} sample clips`;
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
  search?.addEventListener('keydown', event => {
    if (event.key === 'Escape' && search.value) { event.stopPropagation(); search.value = ''; renderClips(); }
  });
  grid?.addEventListener('click', async event => {
    const button = event.target.closest('button[data-action]');
    if (!button) return;
    const clip = clips.find(candidate => candidate.id === Number(button.dataset.id));
    if (!clip) return;
    if (button.dataset.action === 'pin') {
      clip.pinned = !clip.pinned;
      // Update in place to keep keyboard focus, even in the Pinned view.
      button.setAttribute('aria-pressed', String(clip.pinned));
      button.closest('.demo-clip').classList.toggle('is-pinned', clip.pinned);
      button.classList.remove('pop');
      void button.offsetWidth;
      if (motionAllowed()) button.classList.add('pop');
      if (filter === 'pinned' && !clip.pinned) {
        renderClips();
        ($('#demo-clips .demo-clip:not([hidden]) .demo-pin') || $('.demo-filters [data-filter="pinned"]')).focus();
      }
      toast(clip.pinned ? `Pinned “${clip.label}”.` : `Unpinned “${clip.label}”.`);
    } else {
      try {
        await navigator.clipboard.writeText(clip.content);
        const label = button.querySelector('span');
        button.classList.add('done');
        label.textContent = 'Copied';
        setTimeout(() => { button.classList.remove('done'); label.textContent = 'Copy'; }, 1600);
        toast('Copied to your clipboard.');
      } catch {
        toast('Clipboard access is unavailable here. Select the sample text to copy it.');
      }
    }
  });
  $('#demo-reset')?.addEventListener('click', () => {
    clips = initialClips.map(clip => ({ ...clip }));
    cards.clear();
    if (search) search.value = '';
    $('.demo-filters [data-filter="all"]')?.click();
    toast('Sample shelf reset.');
  });
  document.addEventListener('keydown', event => {
    if (event.key !== '/' || event.ctrlKey || event.metaKey || event.altKey || /INPUT|TEXTAREA|SELECT/.test(event.target.tagName) || event.target.isContentEditable) return;
    if (!search) return;
    event.preventDefault();
    search.scrollIntoView({ block: 'center', behavior: motionAllowed() ? 'smooth' : 'instant' });
    search.focus({ preventScroll: true });
  });
  renderClips();

  /* ── Sync illustration ───────────────────────────────────────────────── */
  $('#sync-trigger')?.addEventListener('click', async () => {
    const button = $('#sync-trigger');
    const syncStage = $('.sync-stage');
    button.disabled = true;
    syncStage.classList.remove('synced', 'syncing');
    void syncStage.offsetWidth;
    syncStage.classList.add('syncing');
    $('#sync-state').textContent = 'Sending over your network…';
    await wait(1500);
    $('#sync-target').textContent = $('#sync-source').textContent;
    $('#sync-receiver .sync-kind').textContent = 'Text · just now';
    $('#sync-state').textContent = 'Arrived on your phone. (Illustration)';
    syncStage.classList.remove('syncing');
    syncStage.classList.add('synced');
    button.disabled = false;
    button.querySelector('span').textContent = 'Send again';
  });

  /* ── Tool tiles animate only while on screen ─────────────────────────── */
  const tiles = $$('.tile');
  if ('IntersectionObserver' in window) {
    const tileObserver = new IntersectionObserver(entries => {
      for (const entry of entries) entry.target.classList.toggle('in-view', entry.isIntersecting && motionAllowed());
    }, { threshold: .35 });
    tiles.forEach(tile => tileObserver.observe(tile));
    site.onMotionChange(allowed => {
      if (!allowed) tiles.forEach(tile => tile.classList.remove('in-view'));
      else tiles.forEach(tile => { tileObserver.unobserve(tile); tileObserver.observe(tile); });
    });
  }

  /* ── Drag illustration ───────────────────────────────────────────────── */
  $('#drag-demo')?.addEventListener('click', async () => {
    const button = $('#drag-demo');
    const scene = $('.drag-scene');
    const file = $('.travelling-file', scene);
    const drop = $('.drop-target', scene);
    scene.classList.remove('delivered', 'moving');
    const fileBox = file.getBoundingClientRect();
    const dropBox = drop.getBoundingClientRect();
    scene.style.setProperty('--drag-dx', `${dropBox.left + dropBox.width / 2 - (fileBox.left + fileBox.width / 2)}px`);
    scene.style.setProperty('--drag-dy', `${dropBox.top + dropBox.height / 2 - (fileBox.top + fileBox.height / 2)}px`);
    void scene.offsetWidth;
    scene.classList.add('moving');
    button.disabled = true;
    $('#drag-status').textContent = 'Dragging project-brief.pdf into a new email…';
    await wait(1650);
    scene.classList.remove('moving');
    scene.classList.add('delivered');
    $('.drop-target p', scene).textContent = 'Attached';
    $('#drag-status').textContent = 'Attached. That’s the whole move. (Illustration)';
    button.disabled = false;
    button.querySelector('span').textContent = 'Watch again';
  });

  /* ── Real screenshots ────────────────────────────────────────────────── */
  const shots = {
    clipboard: { src: 'assets/shelf-clipboard.webp', width: 451, height: 477, caption: 'The floating shelf: filters for images, pins, PDFs, documents and passwords, with your newest clips first.', alt: 'FlyShelf’s floating clipboard on Windows, with filter chips and recent file, text and image clips' },
    tools: { src: 'assets/shelf-pdf-studio.webp', width: 1916, height: 1015, caption: 'PDF Studio: 13 local tools to split, merge, compress, protect, compare and convert PDFs.', alt: 'FlyShelf PDF Studio with recent documents and tools including Split, Merge, Rotate, Compress, Protect and PDF to Word' },
    notes: { src: 'assets/shelf-notes.webp', width: 461, height: 483, caption: 'Notes: a page for today, with your to-do list and reminders one click away.', alt: 'FlyShelf notes panel showing a dated note with to-do list and reminder controls' },
    themes: { src: 'assets/shelf-personalization.webp', width: 1141, height: 832, caption: 'Personalization: your own wallpaper, blur and background styles, in the FlyShelf Hub.', alt: 'FlyShelf Hub personalization page with wallpaper, blur effect and background style options' },
  };
  const tabs = $$('.showcase-tabs [role="tab"]');
  const tablist = $('.showcase-tabs');
  const indicator = $('.tab-indicator');
  function placeIndicator() {
    const selected = tabs.find(tab => tab.getAttribute('aria-selected') === 'true');
    if (!selected || !indicator) return;
    indicator.style.width = `${selected.offsetWidth}px`;
    indicator.style.transform = `translateX(${selected.offsetLeft}px)`;
  }
  if (indicator && tablist) {
    tablist.classList.add('has-indicator');
    placeIndicator();
    addEventListener('resize', placeIndicator, { passive: true });
    document.fonts?.ready.then(placeIndicator);
  }
  let shotRequest = 0;
  async function selectShot(button) {
    const shot = shots[button.dataset.shot];
    const request = ++shotRequest;
    tabs.forEach(candidate => {
      const selected = candidate === button;
      candidate.setAttribute('aria-selected', String(selected));
      candidate.tabIndex = selected ? 0 : -1;
    });
    placeIndicator();
    button.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: motionAllowed() ? 'smooth' : 'instant' });
    const preloaded = new Image();
    preloaded.src = shot.src;
    try { await preloaded.decode(); } catch { toast('That screenshot couldn’t load. Please try again.'); return; }
    if (request !== shotRequest) return;
    const image = $('#app-screenshot');
    if (motionAllowed()) {
      image.classList.add('leaving');
      await new Promise(resolve => setTimeout(resolve, 200));
      if (request !== shotRequest) return;
    }
    image.classList.remove('leaving', 'arriving');
    image.src = shot.src; image.alt = shot.alt; image.width = shot.width; image.height = shot.height;
    if (motionAllowed()) { void image.offsetWidth; image.classList.add('arriving'); }
    $('#shot-caption').textContent = shot.caption;
    const panel = $('#app-shot-panel');
    panel.className = `shot-stage reveal visible shot-${button.dataset.shot}`;
    panel.setAttribute('aria-labelledby', button.id);
  }
  tabs.forEach((button, index) => {
    button.addEventListener('click', () => selectShot(button));
    button.addEventListener('keydown', event => {
      const next = event.key === 'ArrowRight' ? (index + 1) % tabs.length
        : event.key === 'ArrowLeft' ? (index + tabs.length - 1) % tabs.length
        : event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 : null;
      if (next === null) return;
      event.preventDefault();
      tabs[next].focus();
      selectShot(tabs[next]);
    });
  });

  /* ── Accent moods ────────────────────────────────────────────────────── */
  let vibe = 0;
  $('#theme-shift')?.addEventListener('click', () => {
    vibe = (vibe + 1) % 3;
    document.body.classList.toggle('vibe-lavender', vibe === 1);
    document.body.classList.toggle('vibe-peach', vibe === 2);
    placeIndicator();
    toast(['Blue — the FlyShelf default.', 'Lavender. The app has its own palettes, too.', 'Peach. Choose yours in the app’s Personalization page.'][vibe]);
  });

  /* ── Lead with the download that fits this device ────────────────────── */
  if (/Android/i.test(navigator.userAgent)) {
    const android = $('#download-android');
    const windows = $('#download-windows');
    if (android && windows) {
      android.classList.add('btn-primary');
      windows.classList.remove('btn-primary');
      android.parentNode.insertBefore(android, windows);
    }
  }
})();
