/* Tiny local icon set for checkout, avoiding remote icon framework downloads. */
(() => {
  if (customElements.get('ion-icon')) return;
  const paths = {
    'briefcase': '<rect x="3" y="7" width="18" height="14" rx="2"/><path d="M8 7V4h8v3M3 12h18M10 12v3h4v-3"/>',
    'lock-closed': '<rect x="5" y="10" width="14" height="11" rx="2"/><path d="M8 10V6a4 4 0 0 1 8 0v4M12 14v3"/>',
    'card': '<rect x="2" y="5" width="20" height="14" rx="3"/><path d="M2 10h20M6 15h3"/>',
    'pin': '<path d="M12 22s7-9 7-14a7 7 0 0 0-14 0c0 5 7 14 7 14Z"/><circle cx="12" cy="8" r="2"/>',
    'globe': '<circle cx="12" cy="12" r="10"/><ellipse cx="12" cy="12" rx="4" ry="10"/><path d="M2 12h20"/>',
    'checkmark-circle': '<circle cx="12" cy="12" r="10"/><path d="m7 12 3 3 7-7"/>',
    'checkmark-done': '<path d="m2 12 5 5 10-10M12 17 22 7"/>',
    'copy': '<rect x="8" y="8" width="13" height="13" rx="2"/><path d="M16 8V3H3v13h5"/>',
    'menu': '<path d="M3 6h18M3 12h18M3 18h18"/>',
    'close': '<path d="m5 5 14 14M19 5 5 19"/>',
    'arrow-forward': '<path d="M3 12h18m-7-7 7 7-7 7"/>',
    'rocket': '<path d="M9 15c-4 0-6 3-6 6 3 0 6-2 6-6Zm0 0c-2-4 1-9 12-12 0 10-5 14-9 13ZM9 8H5l-2 5h5M16 15v4l-5 2v-5"/>',
    'shield-checkmark': '<path d="m12 2 8 4v6c0 5-8 10-8 10S4 17 4 12V6Z"/><path d="m8 11 3 3 5-5"/>',
  };
  class LocalIcon extends HTMLElement {
    static get observedAttributes() { return ['name']; }
    connectedCallback() { this.render(); }
    attributeChangedCallback() { this.render(); }
    render() {
      this.setAttribute('aria-hidden', 'true');
      Object.assign(this.style, { display: 'inline-flex', width: '1.15em', height: '1.15em', verticalAlign: '-.15em', flexShrink: '0' });
      const name = (this.getAttribute('name') || '').replace(/-outline$/, '');
      this.innerHTML = '<svg viewBox="0 0 24 24" width="100%" height="100%" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">' + (paths[name] || paths['checkmark-circle']) + '</svg>';
    }
  }
  customElements.define('ion-icon', LocalIcon);
})();
