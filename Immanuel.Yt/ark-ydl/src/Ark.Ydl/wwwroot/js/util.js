// Small helpers shared by every module. No framework, no build step.

export const $ = (sel, root = document) => root.querySelector(sel);
export const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

// ── safe HTML ────────────────────────────────────────────────────────────
// html`` escapes every interpolation unless it is itself html`` / raw(). Titles and descriptions
// come from YouTube, so nothing is ever concatenated into markup unescaped.
const ESC = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;', '`': '&#96;' };
export const esc = (s) => String(s ?? '').replace(/[&<>"'`]/g, (c) => ESC[c]);
export const raw = (s) => ({ __html: String(s ?? '') });
const part = (v) => {
  if (v == null || v === false || v === true) return '';
  if (Array.isArray(v)) return v.map(part).join('');
  if (typeof v === 'object' && '__html' in v) return v.__html;
  return esc(v);
};
export function html(strings, ...vals) {
  let out = strings[0];
  for (let i = 0; i < vals.length; i++) out += part(vals[i]) + strings[i + 1];
  return raw(out);
}
export function setHTML(el, h) { el.innerHTML = typeof h === 'object' && h && '__html' in h ? h.__html : esc(h); }
export const safeUrl = (u) => (typeof u === 'string' && /^https?:\/\//i.test(u) ? u : '#');

// ── formatting ───────────────────────────────────────────────────────────
export function fmtTime(sec, precise = false) {
  if (sec == null || !isFinite(sec) || sec < 0) sec = 0;
  const whole = Math.floor(sec);
  const h = Math.floor(whole / 3600), m = Math.floor((whole % 3600) / 60), s = whole % 60;
  let out = h > 0 ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}` : `${m}:${String(s).padStart(2, '0')}`;
  if (precise) {
    const frac = Math.round((sec - whole) * 10);
    if (frac > 0 && frac < 10) out += '.' + frac;
  }
  return out;
}

/** "90", "1:30", "01:02:03.5", "1h2m3s", "2m", "45s" → seconds, or null. */
export function parseTime(text) {
  if (text == null) return null;
  const t = String(text).trim().replace(',', '.').toLowerCase();
  if (!t) return null;
  if (t.includes(':')) {
    const parts = t.split(':');
    if (parts.length < 2 || parts.length > 3) return null;
    let total = 0;
    for (let i = 0; i < parts.length; i++) {
      if (!/^\d+(\.\d+)?$/.test(parts[i])) return null;
      const v = parseFloat(parts[i]);
      if (i > 0 && v >= 60) return null;
      total = total * 60 + v;
    }
    return total;
  }
  const m = t.match(/^(?:(\d+(?:\.\d+)?)h)?(?:(\d+(?:\.\d+)?)m)?(?:(\d+(?:\.\d+)?)s?)?$/);
  if (!m || (!m[1] && !m[2] && !m[3])) return null;
  return (parseFloat(m[1] || 0) * 3600) + (parseFloat(m[2] || 0) * 60) + parseFloat(m[3] || 0);
}

export function fmtBytes(n) {
  if (n == null || !isFinite(n) || n <= 0) return '';
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0;
  while (n >= 1000 && i < u.length - 1) { n /= 1024; i++; }
  return `${n >= 100 || i === 0 ? Math.round(n) : n.toFixed(1)} ${u[i]}`;
}
export const fmtSpeed = (bps) => (bps > 0 ? `${fmtBytes(bps)}/s` : '');
export function fmtEta(sec) {
  if (sec == null || !isFinite(sec) || sec < 0) return '';
  if (sec < 1) return 'almost done';
  return `${fmtTime(sec)} left`;
}
export function fmtCount(n) {
  if (n == null) return '';
  try { return new Intl.NumberFormat(undefined, { notation: 'compact', maximumFractionDigits: 1 }).format(n); }
  catch { return String(n); }
}
export function fmtDate(iso) {
  if (!iso) return '';
  try { return new Date(iso + (iso.length === 10 ? 'T00:00:00' : '')).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }); }
  catch { return iso; }
}
export function fmtClock(iso) {
  try { return new Date(iso).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' }); } catch { return ''; }
}
export const plural = (n, one, many = one + 's') => `${n} ${n === 1 ? one : many}`;
export const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

// ── storage (wrapped: private windows and blocked storage must not break the page) ──
export const store = {
  get(key, fallback) {
    try { const v = localStorage.getItem(key); return v == null ? fallback : JSON.parse(v); } catch { return fallback; }
  },
  set(key, value) { try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* ignore */ } },
  del(key) { try { localStorage.removeItem(key); } catch { /* ignore */ } },
};

// ── toasts ───────────────────────────────────────────────────────────────
export function toast(message, kind = 'info', ms = 4200, action = null) {
  const host = $('#toasts');
  if (!host) return;
  const el = document.createElement('div');
  el.className = `toast ${kind}`;
  el.setAttribute('role', kind === 'err' ? 'alert' : 'status');
  setHTML(el, html`<span class="grow">${message}</span>${action ? html`<button type="button" data-act="toast-action">${action.label}</button>` : ''}<button type="button" aria-label="Dismiss" data-act="toast-close">✕</button>`);
  const close = () => { el.remove(); };
  el.addEventListener('click', (e) => {
    const b = e.target.closest('button');
    if (!b) return;
    if (b.dataset.act === 'toast-action') action?.run();
    close();
  });
  host.append(el);
  if (ms) setTimeout(close, ms);
}

export async function copyText(text) {
  try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
}

export function debounce(fn, ms = 200) {
  let t;
  return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); };
}

/** Starts a same-origin download without leaving the page. */
export function triggerDownload(url) {
  const a = document.createElement('a');
  a.href = url;
  a.download = '';
  a.rel = 'noopener';
  document.body.append(a);
  a.click();
  a.remove();
}

// ── icons (inline SVG, stroke = currentColor) ───────────────────────────
const P = {
  download: '<path d="M12 4v11m0 0-4.5-4.5M12 15l4.5-4.5M5 19h14"/>',
  link: '<path d="M10 14a4 4 0 0 0 5.66 0l3-3a4 4 0 0 0-5.66-5.66l-1 1M14 10a4 4 0 0 0-5.66 0l-3 3a4 4 0 0 0 5.66 5.66l1-1"/>',
  paste: '<rect x="6" y="4" width="12" height="17" rx="2"/><path d="M9 4.5V3h6v1.5M9 10h6M9 14h6"/>',
  x: '<path d="M6 6l12 12M18 6 6 18"/>',
  arrow: '<path d="M5 12h14m0 0-5-5m5 5-5 5"/>',
  film: '<rect x="3" y="5" width="18" height="14" rx="2.5"/><path d="M7 5v14M17 5v14M3 9.5h4M17 9.5h4M3 14.5h4M17 14.5h4"/>',
  music: '<path d="M9 18V6l11-2v12"/><circle cx="6.5" cy="18" r="2.5"/><circle cx="17.5" cy="16" r="2.5"/>',
  scissors: '<circle cx="6" cy="7" r="2.6"/><circle cx="6" cy="17" r="2.6"/><path d="M8.2 8.4 20 17M8.2 15.6 20 7"/>',
  list: '<path d="M9 6h11M9 12h11M9 18h11"/><circle cx="4.5" cy="6" r="1"/><circle cx="4.5" cy="12" r="1"/><circle cx="4.5" cy="18" r="1"/>',
  cc: '<rect x="3" y="5" width="18" height="14" rx="3"/><path d="M10.5 10.2a2.4 2.4 0 1 0 0 3.6M17 10.2a2.4 2.4 0 1 0 0 3.6"/>',
  skip: '<path d="M5 5l9 7-9 7V5zM18 5v14"/>',
  theme: '<circle cx="12" cy="12" r="8"/><path d="M12 4a8 8 0 0 0 0 16z" fill="currentColor"/>',
  check: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
  play: '<path d="M7 5l12 7-12 7V5z"/>',
  pause: '<path d="M8 5v14M16 5v14"/>',
  trash: '<path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3"/>',
  retry: '<path d="M4 12a8 8 0 1 0 2.4-5.7M4 4v4.5h4.5"/>',
  zip: '<rect x="5" y="3" width="14" height="18" rx="2"/><path d="M12 3v2m0 2v2m0 2v2m-1.5 2h3v3h-3z"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 7.5v.5"/>',
  warn: '<path d="M12 4 2.5 20h19L12 4z"/><path d="M12 10v4.5M12 17.5v.5"/>',
  external: '<path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/>',
  image: '<rect x="3" y="4" width="18" height="16" rx="2.5"/><circle cx="9" cy="10" r="2"/><path d="m21 16-5-5-9 9"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  copy: '<rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V5a1 1 0 0 0-1-1H5a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h3"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  swap: '<path d="M7 7h12m0 0-3.5-3.5M19 7l-3.5 3.5M17 17H5m0 0 3.5 3.5M5 17l3.5-3.5"/>',
  bookmark: '<path d="M7 4h10v16l-5-4-5 4V4z"/>',
  keyboard: '<rect x="3" y="6" width="18" height="12" rx="2.5"/><path d="M7 10h.01M11 10h.01M15 10h.01M7 14h10"/>',
  install: '<path d="M12 3v12m0 0-4-4m4 4 4-4M4 17v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-2"/>',
  stamp: '<path d="M12 3v9m0 0-3-3m3 3 3-3"/><path d="M4 17h16v4H4z"/>',
  sparkle: '<path d="M12 3v4M12 17v4M3 12h4M17 12h4M6 6l2.5 2.5M15.5 15.5 18 18M6 18l2.5-2.5M15.5 8.5 18 6"/>',
  sub: '<path d="M4 6h16M4 12h10M4 18h7"/>',
  chapters: '<path d="M4 6h3M4 12h3M4 18h3M10 6h10M10 12h10M10 18h10"/>',
};
export const iconSvg = (name, cls = 'ico') =>
  `<svg class="${cls}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${P[name] || ''}</svg>`;
export const icon = (name) => raw(iconSvg(name));

/** Replaces every <span data-icon="name"> in static markup with its SVG. */
export function hydrateIcons(root = document) {
  for (const el of $$('[data-icon]', root)) {
    el.innerHTML = iconSvg(el.dataset.icon);
    el.removeAttribute('data-icon');
    el.style.display = 'inline-flex';
  }
}
