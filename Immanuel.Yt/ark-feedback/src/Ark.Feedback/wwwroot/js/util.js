// Small helpers shared by every module. No framework, no build step.
export const $ = (sel, root = document) => root.querySelector(sel);
export const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

// ── safe HTML: html`` escapes every interpolation unless it is itself html`` / raw() ──
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
const compact = new Intl.NumberFormat(undefined, { notation: 'compact', maximumFractionDigits: 1 });
const whole = new Intl.NumberFormat(undefined);
export const fmt = {
  count: (n) => (n == null || !isFinite(n) ? '–' : Math.abs(n) < 10000 ? whole.format(Math.round(n)) : compact.format(n)),
  full: (n) => (n == null || !isFinite(n) ? '–' : whole.format(Math.round(n))),
  pct: (x, d = 0) => (x == null || !isFinite(x) ? '–' : `${Number(x).toFixed(d)}%`),
  signed: (x, d = 0) => (x == null || !isFinite(x) ? '–' : `${x > 0 ? '+' : ''}${Number(x).toFixed(d)}`),
  ms: (ms) => {
    if (ms == null || !isFinite(ms)) return '–';
    if (ms < 1000) return `${Math.round(ms)} ms`;
    const s = ms / 1000;
    if (s < 90) return `${s.toFixed(s < 10 ? 1 : 0)} s`;
    const m = Math.floor(s / 60);
    return m < 90 ? `${m} min ${Math.round(s % 60)} s` : `${Math.floor(m / 60)} h ${m % 60} min`;
  },
  duration: (sec) => {
    if (sec == null || !isFinite(sec)) return '';
    const s = Math.round(sec), h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60;
    return h ? `${h}:${String(m).padStart(2, '0')}:${String(r).padStart(2, '0')}` : `${m}:${String(r).padStart(2, '0')}`;
  },
  date: (iso) => {
    if (!iso) return '';
    const d = new Date(iso);
    return isNaN(d) ? '' : d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
  },
  dateTime: (iso) => {
    if (!iso) return '';
    const d = new Date(iso.endsWith('Z') || iso.includes('+') ? iso : iso.replace(' ', 'T') + 'Z');
    return isNaN(d) ? String(iso) : d.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
  },
  ago: (iso) => {
    if (!iso) return '';
    const t = new Date(iso).getTime();
    if (isNaN(t)) return '';
    const s = Math.max(0, (Date.now() - t) / 1000);
    if (s < 60) return 'just now';
    if (s < 3600) return `${Math.floor(s / 60)} min ago`;
    if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
    const d = Math.floor(s / 86400);
    return d < 30 ? `${d} day${d === 1 ? '' : 's'} ago` : fmt.date(iso);
  },
  bytes: (n) => {
    if (n == null || !isFinite(n)) return '–';
    if (n <= 0) return '0 B';
    const u = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0;
    while (n >= 1000 && i < u.length - 1) { n /= 1024; i++; }
    return `${n >= 100 || i === 0 ? Math.round(n) : n.toFixed(1).replace(/\.0$/, '')} ${u[i]}`;
  },
};

export const VERDICTS = {
  loved: 'Loved', liked: 'Liked', 'leaning-positive': 'Leaning positive', mixed: 'Mixed', divided: 'Divided',
  'leaning-negative': 'Leaning negative', disliked: 'Disliked', rejected: 'Rejected', thin: 'Too few comments', none: 'No comments',
};
export const verdictName = (v) => VERDICTS[v] || v || '–';
export const scoreClass = (s) => (s == null ? 'neu' : s >= 10 ? 'pos' : s <= -10 ? 'neg' : 'neu');

// ── toasts & theme ────────────────────────────────────────────────────────
export function toast(msg, ms = 3200) {
  const box = $('#toasts');
  if (!box) return;
  const t = document.createElement('div');
  t.className = 'toast';
  t.textContent = msg;
  box.append(t);
  setTimeout(() => t.remove(), ms);
}
export function initTheme() {
  const key = 'ark-theme';
  let saved = null;
  try { saved = localStorage.getItem(key); } catch { /* private mode */ }
  if (saved === 'dark' || saved === 'light') document.documentElement.dataset.theme = saved;
  $('#btn-theme')?.addEventListener('click', () => {
    const dark = document.documentElement.dataset.theme === 'dark' || (!document.documentElement.dataset.theme && matchMedia('(prefers-color-scheme: dark)').matches);
    const next = dark ? 'light' : 'dark';
    document.documentElement.dataset.theme = next;
    try { localStorage.setItem(key, next); } catch { /* ignore */ }
  });
}
export const debounce = (fn, ms = 250) => { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; };
export const store = {
  get(k, fallback) { try { const v = localStorage.getItem(k); return v == null ? fallback : JSON.parse(v); } catch { return fallback; } },
  set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch { /* ignore */ } },
};
