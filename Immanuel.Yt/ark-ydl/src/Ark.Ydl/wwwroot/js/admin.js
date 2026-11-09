// ARK-RAPID admin console: sign-in gate, one date-range filter row, and tabs of predefined reports over the audit log.
// Every chart has a table view; logs page through the server; nothing from the log reaches the DOM unescaped.
import { $, $$, html, setHTML, icon, hydrateIcons, store, toast, fmtDate, triggerDownload, debounce } from './util.js';
import { request } from './api.js';
import { fmt, columns, line, hbars, segmented, heatmap, statTile, table, hideTip } from './charts.js';
import { compatBadge, changes, planHtml, cmpVersion } from './release.js';

const THEMES = ['auto', 'dark', 'light'];
const PRESETS = { 1: 'Today', 7: '7 days', 30: '30 days', 90: '90 days', 365: '12 months' };
const TAB_NEEDS = {
  overview: ['overview', 'breakdowns'],
  content: ['breakdowns'],
  audience: ['overview', 'breakdowns'],
  downloads: ['downloads'],
  requests: ['breakdowns', 'requests'],
  releases: ['releases', 'status'],
  system: ['status'],
};

const A = {
  session: null,
  today: null,
  tab: 'overview',
  preset: '30',
  from: null,
  to: null,
  cache: {},        // key → data for the current range
  dl: { page: 1, filters: {} },
  rq: { page: 1, filters: {} },
  timer: null,
  loading: 0,
};

// ── boot ────────────────────────────────────────────────────────────────────
function init() {
  hydrateIcons();
  applyTheme(store.get('ydl.theme', 'auto'));
  readHash();
  bind();
  loadSession();
}

async function loadSession() {
  let s;
  try {
    s = await request('GET', '/api/admin/session');
  } catch (err) {
    showGate({ error: err.message });
    return;
  }
  A.session = s;
  A.today = s.today;
  $('#audit-tz').textContent = s.timeZone || 'UTC';
  if (s.signedIn) showConsole(); else showGate(s);
}

function showGate(s) {
  $('#console').hidden = true;
  $('#gate').hidden = false;
  $('#btn-signout').hidden = true;
  $('#who').hidden = true;
  const form = $('#key-form');
  form.hidden = !s.keyConfigured;
  const help = [];
  if (s.error) help.push(html`<div class="banner err">${icon('warn')}<p class="grow">${s.error}</p></div>`);
  if (s.keyProblem) help.push(html`<div class="banner warn">${icon('warn')}<p class="grow">${s.keyProblem}</p></div>`);
  if (s.roleSignIn) {
    const roles = s.roles?.length ? s.roles : ['ark-ydl.admin'];
    help.push(html`<p>${icon('info')} Signed in as <b>${s.user || 'an Ark user'}</b>, without an admin claim. Ask for the Ark claim
      ${roles.map((r, i) => html`${i ? ' or ' : ''}<code>${r}</code>`)} on this app's client${s.keyConfigured ? ', or use the admin key' : ''}.</p>`);
  }
  if (!s.keyConfigured && !s.roleSignIn && !s.error)
    help.push(html`<p>The console is locked: no admin key is configured. Set <code>Ydl:Admin:Key</code> (16+ characters) in <code>appsettings.Production.json</code>, or the <code>Ydl__Admin__Key</code> environment variable, then restart the app. With Ark sign-in on, the Ark claim <code>ark-ydl.admin</code> also opens it.</p>`);
  if (s.auditEnabled === false) help.push(html`<p>${icon('info')} Auditing is off on this server (<code>Ydl:Audit:Enabled</code>), so there is nothing to report.</p>`);
  setHTML($('#gate-help'), html`${help}`);
  $('#gate-text').textContent = s.keyConfigured ? 'Enter the admin key to see the audit log and its reports.' : 'Sign-in is needed to see the audit log and its reports.';
  if (s.keyConfigured) setTimeout(() => $('#key').focus(), 50);
}

function showConsole() {
  const s = A.session;
  $('#gate').hidden = true;
  $('#console').hidden = false;
  const who = $('#who');
  who.hidden = false;
  who.textContent = s.method === 'role' ? (s.name || 'Ark admin') : s.method === 'development' ? 'local development' : 'admin key';
  who.className = 'pill' + (s.method === 'development' ? ' warn' : '');
  $('#btn-signout').hidden = s.method !== 'key';
  if (s.method === 'development') notice('Open without a key because this is a Development run on this machine. Set Ydl:Admin:Key before deploying.', 'warn');
  prepareCards();
  syncRangeUi();
  selectTab(A.tab, false);
  refresh();
  scheduleAuto();
}

function notice(text, kind = 'warn') {
  const n = $('#notice');
  if (!text) { n.hidden = true; return; }
  n.className = `banner ${kind}`;
  setHTML(n, html`${icon(kind === 'err' ? 'warn' : 'info')}<p class="grow">${text}</p>`);
  n.hidden = false;
}

// ── range & tabs ────────────────────────────────────────────────────────────
function addDays(day, n) {
  const d = new Date(`${day}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
}

function currentRange() {
  if (A.preset === 'custom' && A.from && A.to) return { from: A.from, to: A.to };
  const n = parseInt(A.preset, 10) || 30;
  const today = A.today || new Date().toISOString().slice(0, 10);
  return { from: addDays(today, -(n - 1)), to: today };
}
const rangeQuery = () => { const r = currentRange(); return `from=${r.from}&to=${r.to}`; };
const rangeKey = () => { const r = currentRange(); return `${r.from}_${r.to}`; };

function syncRangeUi() {
  for (const b of $$('#range-seg button')) b.setAttribute('aria-pressed', String(b.dataset.range === A.preset));
  $('#custom-range').hidden = A.preset !== 'custom';
  const r = currentRange();
  $('#from').value = r.from;
  $('#to').value = r.to;
  writeHash();
}

function readHash() {
  const q = new URLSearchParams(location.hash.slice(1));
  if (TAB_NEEDS[q.get('tab')]) A.tab = q.get('tab');
  const range = q.get('range');
  if (range && (PRESETS[range] || range === 'custom')) A.preset = range;
  if (/^\d{4}-\d{2}-\d{2}$/.test(q.get('from') || '')) A.from = q.get('from');
  if (/^\d{4}-\d{2}-\d{2}$/.test(q.get('to') || '')) A.to = q.get('to');
}
function writeHash() {
  const q = new URLSearchParams({ tab: A.tab, range: A.preset });
  if (A.preset === 'custom') { const r = currentRange(); q.set('from', r.from); q.set('to', r.to); }
  history.replaceState(null, '', `#${q}`);
}

function selectTab(tab, load = true) {
  A.tab = tab;
  for (const b of $$('.tabs [role="tab"]')) b.setAttribute('aria-selected', String(b.dataset.tab === tab));
  for (const p of $$('.panel')) p.hidden = p.id !== `p-${tab}`;
  hideTip();
  writeHash();
  if (load) refresh(false);
}

// ── data ────────────────────────────────────────────────────────────────────
async function fetchPart(part, force) {
  const key = ['releases', 'status'].includes(part) ? part : `${part}:${rangeKey()}`;
  const paged = part === 'downloads' || part === 'requests';
  if (!force && !paged && A.cache[key]) return A.cache[key];
  let data;
  switch (part) {
    case 'overview': data = await request('GET', `/api/admin/overview?${rangeQuery()}`); break;
    case 'breakdowns': data = (await request('GET', `/api/admin/breakdowns?${rangeQuery()}`)).data; break;
    case 'downloads': data = await request('GET', `/api/admin/downloads?${rangeQuery()}&${logQuery(A.dl)}`); break;
    case 'requests': data = await request('GET', `/api/admin/requests?${rangeQuery()}&${logQuery(A.rq)}`); break;
    case 'releases': data = await request('GET', '/api/releases'); break;
    case 'status': data = await request('GET', '/api/admin/status'); break;
  }
  A.cache[key] = data;
  return data;
}

function logQuery(state) {
  const q = new URLSearchParams({ page: String(state.page), size: '50' });
  for (const [k, v] of Object.entries(state.filters)) if (v) q.set(k, v);
  return q.toString();
}

/** Loads what the visible tab needs and draws it. The previous render stays (dimmed) until the new data is in. */
async function refresh(force = true) {
  const tab = A.tab;
  const panel = $(`#p-${tab}`);
  panel.classList.add('loading');
  const seq = ++A.loading;
  try {
    const parts = TAB_NEEDS[tab];
    const got = {};
    await Promise.all(parts.map(async (p) => { got[p] = await fetchPart(p, force); }));
    if (seq !== A.loading || tab !== A.tab) return;
    if (A.session?.method !== 'development') notice(null);
    RENDER[tab](got);
    $('#updated').textContent = `Updated ${new Date().toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })}`;
  } catch (err) {
    if (err.status === 401) { A.session = null; loadSession(); return; }
    notice(err.message, err.status === 503 ? 'warn' : 'err');
  } finally {
    if (seq === A.loading) panel.classList.remove('loading');
  }
}

function scheduleAuto() {
  clearInterval(A.timer);
  A.timer = setInterval(() => {
    if (!$('#auto').checked || document.visibilityState !== 'visible' || $('#console').hidden) return;
    if (!['overview', 'audience', 'downloads', 'requests'].includes(A.tab)) return;
    if (A.today && currentRange().to < A.today) return; // a range in the past doesn't change
    refresh(true);
  }, 60_000);
}

// ── chart cards ─────────────────────────────────────────────────────────────
const drawn = new Map(); // card name → () => void, to redraw on resize

function prepareCards() {
  for (const card of $$('[data-chart]')) {
    if (card.querySelector('.chart-area')) continue;
    const tableOnly = card.dataset.chart === 'videos';
    if (!tableOnly) {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'ghost small view-toggle';
      btn.textContent = 'Table';
      btn.setAttribute('aria-pressed', 'false');
      btn.addEventListener('click', () => {
        const showTable = btn.getAttribute('aria-pressed') !== 'true';
        btn.setAttribute('aria-pressed', String(showTable));
        btn.textContent = showTable ? 'Chart' : 'Table';
        card.querySelector('.chart-area').hidden = showTable;
        card.querySelector('.table-area').hidden = !showTable;
      });
      card.querySelector('header').append(btn);
    }
    const chart = document.createElement('div');
    chart.className = 'chart-area';
    chart.hidden = tableOnly;
    const tbl = document.createElement('div');
    tbl.className = 'table-area';
    tbl.hidden = !tableOnly;
    card.append(chart, tbl);
  }
}

function card(name) {
  const c = $(`[data-chart="${name}"]`);
  return { card: c, chart: c.querySelector('.chart-area'), table: c.querySelector('.table-area'), sub: c.querySelector('[data-sub]') };
}

/** Draws a chart now and again whenever the window is resized. */
function draw(name, fn) {
  const c = card(name);
  const run = () => fn(c);
  drawn.set(name, run);
  run();
}

// ── labels ──────────────────────────────────────────────────────────────────
const MONTH = { month: 'short', day: 'numeric', timeZone: 'UTC' };
const shortDay = (d) => new Date(`${d}T00:00:00Z`).toLocaleDateString(undefined, MONTH);
const longDay = (d) => new Date(`${d}T00:00:00Z`).toLocaleDateString(undefined, { weekday: 'short', year: 'numeric', ...MONTH });
const when = (utc) => {
  if (!utc) return '';
  const d = new Date(`${String(utc).replace(' ', 'T')}Z`);
  return isNaN(d) ? utc : d.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
};
const whenFull = (utc) => {
  if (!utc) return '';
  const d = new Date(`${String(utc).replace(' ', 'T')}Z`);
  return isNaN(d) ? utc : d.toLocaleString();
};
const DEVICE = { desktop: ['Desktop', 'var(--series-1)'], mobile: ['Phone', 'var(--series-2)'], tablet: ['Tablet', 'var(--series-3)'], bot: ['Bot / script', 'var(--series-4)'] };
const MODE = { video: ['Video', 'var(--series-1)'], audio: ['Audio only', 'var(--series-2)'], subtitles: ['Subtitles', 'var(--series-3)'], thumbnail: ['Thumbnail', 'var(--series-4)'] };
const SCOPE = { whole: ['Whole video', 'var(--series-1)'], clip: ['Clip', 'var(--series-2)'], chapters: ['Chapters', 'var(--series-3)'], playlist: ['Playlist', 'var(--series-4)'] };
const STATUS = { completed: ['Completed', 'var(--viz-good)'], canceled: ['Canceled', 'var(--viz-neutral)'], failed: ['Failed', 'var(--viz-critical)'] };
const HTTP = { 200: 'OK', 202: 'Accepted', 204: 'No content', 206: 'Partial', 301: 'Moved', 302: 'Redirect', 304: 'Not modified', 400: 'Bad request', 401: 'Unauthorized', 403: 'Forbidden', 404: 'Not found', 405: 'Method not allowed', 410: 'Gone', 422: 'Unprocessable', 429: 'Too many requests', 499: 'Client closed', 500: 'Server error', 502: 'Bad gateway', 503: 'Unavailable', 504: 'Timeout' };
const rows = (list, map = (x) => x.key) => (list || []).map((x) => ({ label: map(x) ?? 'Unknown', value: x.count, bytes: x.bytes, avg: x.avg, key: x.key, extra: x.label }));
/** Parts in a fixed order with fixed colours, so a colour always means the same thing (it follows the entity). */
function fixedParts(list, dict) {
  const byKey = new Map((list || []).map((x) => [x.key, x.count]));
  const parts = Object.entries(dict).map(([k, [label, color]]) => ({ label, color, value: byKey.get(k) || 0 }));
  const other = (list || []).filter((x) => !dict[x.key]).reduce((a, x) => a + x.count, 0);
  if (other) parts.push({ label: 'Other', value: other, other: true });
  return parts;
}
const kvTable = (head, valueFmt = fmt.full, extra = []) => [{ key: 'label', label: head }, { key: 'value', label: 'Count', num: true, format: valueFmt }, ...extra];
const bytesCol = { key: 'bytes', label: 'Size', num: true, format: (v) => (v ? fmt.bytes(v) : '–') };

function series(o) {
  const hourly = o.hourly;
  if (hourly) {
    const multiDay = new Set(hourly.map((h) => h.day)).size > 1;
    return {
      labels: hourly.map((h) => (multiDay && h.hour === 0 ? shortDay(h.day) : `${String(h.hour).padStart(2, '0')}h`)),
      long: hourly.map((h) => `${longDay(h.day)} · ${String(h.hour).padStart(2, '0')}:00–${String((h.hour + 1) % 24).padStart(2, '0')}:00`),
      rows: hourly, unit: 'hour',
    };
  }
  return { labels: o.daily.map((d) => shortDay(d.day)), long: o.daily.map((d) => longDay(d.day)), rows: o.daily, unit: 'day' };
}

function delta(cur, prev) { return prev > 0 ? (cur - prev) / prev : cur > 0 ? null : 0; }

// ── renderers per tab ───────────────────────────────────────────────────────
const RENDER = {
  overview({ overview: o, breakdowns: b }) {
    const k = o.kpis, p = o.previousKpis;
    const s = series(o);
    const spark = (key) => o.daily.map((d) => d[key]);
    const tiles = [
      { label: 'Downloads', value: fmt.count(k.downloads), delta: delta(k.downloads, p.downloads), spark: spark('downloads') },
      { label: 'Success rate', value: k.completed + k.failed ? fmt.pct(k.successRate) : '–',
        delta: k.completed + k.failed && p.completed + p.failed ? k.successRate - p.successRate : null,
        deltaText: `${Math.abs((k.successRate - p.successRate) * 100).toFixed(1)} pts`, note: `${fmt.count(k.failed)} failed` },
      { label: 'Data prepared', value: fmt.bytes(k.bytes), delta: delta(k.bytes, p.bytes), spark: spark('bytes') },
      { label: 'Saved to devices', value: fmt.count(k.servedCount), delta: delta(k.servedCount, p.servedCount), note: fmt.bytes(k.servedBytes) },
      { label: 'Visitors', value: fmt.count(k.visitors), delta: delta(k.visitors, p.visitors), spark: spark('visitors') },
      { label: 'Videos', value: fmt.count(k.videos), delta: delta(k.videos, p.videos), note: `${fmt.count(k.downloaders)} downloaders` },
      { label: 'Media downloaded', value: fmt.secs(k.mediaSeconds), delta: delta(k.mediaSeconds, p.mediaSeconds) },
      { label: 'Average file', value: k.avgBytes ? fmt.bytes(k.avgBytes) : '–', note: `processing ${fmt.ms(k.avgRunMs)} on average`, delta: null },
    ];
    renderTiles($('#kpis'), tiles);

    draw('outcomes', (c) => {
      c.sub.textContent = `Per ${s.unit}; completed, canceled and failed stacked`;
      const ser = Object.entries(STATUS).map(([key, [label, color]]) => ({
        label, color, icon: key === 'completed' ? '✓' : key === 'failed' ? '✕' : '–', values: s.rows.map((r) => r[key]),
      }));
      columns(c.chart, { labels: s.labels, long: s.long, series: ser });
      table(c.table, [{ key: 'when', label: s.unit === 'hour' ? 'Hour' : 'Day' }, ...ser.map((x, i) => ({ key: `v${i}`, label: x.label, num: true, format: fmt.full })), { key: 'all', label: 'All', num: true, format: fmt.full }],
        s.rows.map((r, i) => ({ when: s.long[i], v0: r.completed, v1: r.canceled, v2: r.failed, all: r.downloads })));
    });
    draw('visitors', (c) => {
      line(c.chart, { labels: s.labels, long: s.long, values: s.rows.map((r) => r.visitors), label: 'visitors' });
      table(c.table, [{ key: 'when', label: s.unit === 'hour' ? 'Hour' : 'Day' }, { key: 'v', label: 'Visitors', num: true, format: fmt.full }, { key: 'r', label: 'Requests', num: true, format: fmt.full }],
        s.rows.map((r, i) => ({ when: s.long[i], v: r.visitors, r: r.requests })));
    });
    draw('volume', (c) => {
      columns(c.chart, { labels: s.labels, long: s.long, series: [{ label: 'Data prepared', color: 'var(--series-1)', values: s.rows.map((r) => r.bytes) }], format: fmt.bytes, integer: false });
      table(c.table, [{ key: 'when', label: s.unit === 'hour' ? 'Hour' : 'Day' }, { key: 'b', label: 'Size', num: true, format: fmt.bytes }],
        s.rows.map((r, i) => ({ when: s.long[i], b: r.bytes })));
    });
    draw('genres', (c) => { const r = rows(b.genres); hbars(c.chart, { rows: r, max: 8 }); table(c.table, kvTable('Genre', fmt.full, [bytesCol]), r); });
    draw('countries', (c) => { const r = rows(b.countries); hbars(c.chart, { rows: r, max: 8 }); table(c.table, kvTable('Country'), r); });
    draw('devices', (c) => { const parts = fixedParts(b.devices, DEVICE); segmented(c.chart, { parts }); table(c.table, kvTable('Device'), parts.map((x) => ({ label: x.label, value: x.value }))); });
    draw('heatmap', (c) => {
      c.sub.textContent = `Downloads by weekday and hour (${A.session?.timeZone || 'UTC'})`;
      heatmap(c.chart, { cells: b.heatmap, noun: 'downloads' });
      heatTable(c.table, b.heatmap, 'Downloads');
    });
  },

  content({ breakdowns: b }) {
    draw('genresAll', (c) => { const r = rows(b.genres); hbars(c.chart, { rows: r, max: 15 }); table(c.table, kvTable('Genre', fmt.full, [bytesCol]), r); });
    draw('formats', (c) => { const r = rows(b.formats); hbars(c.chart, { rows: r, max: 10 }); table(c.table, kvTable('Format', fmt.full, [bytesCol]), r); });
    draw('modes', (c) => { const parts = fixedParts(b.modes, MODE); segmented(c.chart, { parts }); table(c.table, kvTable('Mode'), parts.map((x) => ({ label: x.label, value: x.value }))); });
    draw('scopes', (c) => { const parts = fixedParts(b.scopes, SCOPE); segmented(c.chart, { parts }); table(c.table, kvTable('Scope'), parts.map((x) => ({ label: x.label, value: x.value }))); });
    draw('qualities', (c) => {
      const r = rows(b.qualities, (x) => (x.key === '0' ? 'Best available' : `${x.key}p${+x.key >= 4320 ? ' (8K)' : +x.key >= 2160 ? ' (4K)' : +x.key >= 1440 ? ' (2K)' : ''}`))
        .sort((a, z) => (+z.key || 99999) - (+a.key || 99999));
      hbars(c.chart, { rows: r, max: 12, other: false });
      table(c.table, kvTable('Quality cap'), r);
    });
    draw('channels', (c) => { const r = rows(b.channels); hbars(c.chart, { rows: r, max: 10 }); table(c.table, kvTable('Channel', fmt.full, [bytesCol]), r); });
    draw('videos', (c) => {
      table(c.table, [
        { key: 'title', label: 'Video', value: (v) => v.title || v.videoId },
        { key: 'channel', label: 'Channel' },
        { key: 'category', label: 'Genre', format: (v) => v || '–' },
        { key: 'count', label: 'Downloads', num: true, format: fmt.full },
        { key: 'downloaders', label: 'People', num: true, format: fmt.full },
        { key: 'bytes', label: 'Size', num: true, format: (v) => (v ? fmt.bytes(v) : '–') },
      ], b.videos || [], { empty: 'No downloads in this period.' });
      linkVideos(c.table, b.videos || []);
    });
    draw('errors', (c) => { const r = rows(b.errors); hbars(c.chart, { rows: r, max: 8, color: 'var(--viz-critical)', empty: 'No failed downloads in this period.' }); table(c.table, kvTable('Reason'), r); });
    draw('statuses', (c) => { const parts = fixedParts(b.statuses, STATUS); segmented(c.chart, { parts }); table(c.table, kvTable('Outcome'), parts.map((x) => ({ label: x.label, value: x.value }))); });
  },

  audience({ overview: o, breakdowns: b }) {
    const k = o.kpis, p = o.previousKpis;
    const installed = (b.installed || []).reduce((a, x) => ({ ...a, [x.key]: x.count }), {});
    const instTotal = (installed['1'] || 0) + (installed['0'] || 0);
    const devices = (b.devices || []).reduce((a, x) => a + x.count, 0);
    const bots = (b.devices || []).find((x) => x.key === 'bot')?.count || 0;
    renderTiles($('#audience-kpis'), [
      { label: 'Visitors', value: fmt.count(k.visitors), delta: delta(k.visitors, p.visitors), spark: o.daily.map((d) => d.visitors) },
      { label: 'Page views', value: fmt.count(k.pageViews), delta: delta(k.pageViews, p.pageViews), spark: o.daily.map((d) => d.pageViews) },
      { label: 'Countries', value: fmt.count(k.countries), delta: delta(k.countries, p.countries) },
      { label: 'Signed-in users', value: fmt.count(k.users), delta: delta(k.users, p.users) },
      { label: 'Use the installed app', value: instTotal ? fmt.pct((installed['1'] || 0) / instTotal) : '–', note: `of ${fmt.count(instTotal)} visitors who reported it` },
      { label: 'Bots and scripts', value: devices ? fmt.pct(bots / devices) : '–', note: `${fmt.count(bots)} of ${fmt.count(devices)} visitors` },
    ]);
    const simple = (name, list, head, map) => draw(name, (c) => { const r = rows(list, map); hbars(c.chart, { rows: r, max: 10 }); table(c.table, kvTable(head), r); });
    simple('visitorCountries', b.visitorCountries, 'Country');
    draw('cities', (c) => { const r = rows(b.cities, (x) => (x.label ? `${x.key}, ${x.label}` : x.key)); hbars(c.chart, { rows: r, max: 10 }); table(c.table, kvTable('City'), r); });
    simple('languages', b.languages, 'Language', (x) => langName(x.key));
    simple('timezones', b.timezones, 'Time zone');
    simple('browsers', b.browsers, 'Browser');
    simple('os', b.os, 'Operating system');
    draw('devices2', (c) => { const parts = fixedParts(b.devices, DEVICE); segmented(c.chart, { parts }); table(c.table, kvTable('Device'), parts.map((x) => ({ label: x.label, value: x.value }))); });
    simple('referrers', b.referrers, 'Referrer', (x) => x.key?.replace(/^https?:\/\//, ''));
    draw('users', (c) => {
      const r = rows(b.users);
      hbars(c.chart, { rows: r, max: 10, empty: A.session?.roleSignIn ? 'No signed-in downloads in this period.' : 'Ark sign-in is off, so downloads are anonymous.' });
      table(c.table, kvTable('User', fmt.full, [bytesCol]), r);
    });
    draw('visitHeat', (c) => { heatmap(c.chart, { cells: b.requestHeatmap, noun: 'page views' }); heatTable(c.table, b.requestHeatmap, 'Page views'); });
  },

  downloads({ downloads: d }) {
    const host = $('#dl-table');
    table(host, [
      { key: 'created_utc', label: 'When', format: when },
      { key: 'status', label: 'Outcome' },
      { key: 'title', label: 'Video' },
      { key: 'category', label: 'Genre', format: (v) => v || '–' },
      { key: 'format', label: 'Format' },
      { key: 'total_bytes', label: 'Size', num: true, format: (v) => (v ? fmt.bytes(v) : '–') },
      { key: 'place', label: 'Where', value: (r) => [r.ip_city, r.ip_country_name].filter(Boolean).join(', ') || '–' },
      { key: 'device', label: 'Device', value: (r) => [r.browser, r.os].filter(Boolean).join(' · ') || r.device || '–' },
      { key: 'who', label: 'Who', value: (r) => r.user_name || r.ip || '–' },
    ], d.rows, { empty: 'No downloads match.' });
    decorateRows(host, d.rows, (tr, r) => {
      tr.cells[1].replaceChildren(stateBadge(r.status));
      const t = tr.cells[2];
      t.className = 't';
      t.textContent = r.title || r.url || r.job_id;
      t.title = r.title || '';
      if (r.channel) { const s = document.createElement('small'); s.textContent = r.channel + (r.error ? ` · ${r.error}` : ''); t.append(s); }
      else if (r.error) { const s = document.createElement('small'); s.textContent = r.error; t.append(s); }
    }, (r) => openDownload(r.job_id));
    pager($('#dl-pager'), d, A.dl);
  },

  requests({ breakdowns: b, requests: d }) {
    draw('requestKinds', (c) => {
      const r = rows(b.requestKinds);
      hbars(c.chart, { rows: r.map((x) => ({ ...x, sub: `${fmt.ms(x.avg)} on average` })), max: 12 });
      table(c.table, kvTable('Kind', fmt.full, [{ key: 'avg', label: 'Average time', num: true, format: fmt.ms }]), r);
    });
    draw('statusCodes', (c) => {
      const r = rows(b.statusCodes, (x) => `${x.key} ${HTTP[x.key] || ''}`.trim());
      hbars(c.chart, { rows: r, max: 10 });
      table(c.table, kvTable('Status'), r);
    });
    const host = $('#rq-table');
    table(host, [
      { key: 'ts_utc', label: 'When', format: when },
      { key: 'req', label: 'Request', value: (r) => `${r.method} ${r.path}` },
      { key: 'kind', label: 'Kind' },
      { key: 'status', label: 'Status', num: true },
      { key: 'duration_ms', label: 'Time', num: true, format: fmt.ms },
      { key: 'place', label: 'Where', value: (r) => [r.ip_city, r.ip_country_name].filter(Boolean).join(', ') || '–' },
      { key: 'device', label: 'Device', value: (r) => [r.browser, r.os].filter(Boolean).join(' · ') || r.device || '–' },
      { key: 'who', label: 'Who', value: (r) => r.user_name || r.ip || '–' },
      { key: 'detail', label: 'Detail', format: (v) => v || '' },
    ], d.rows, { empty: 'No requests match.' });
    decorateRows(host, d.rows, (tr) => { tr.cells[1].className = 't'; tr.cells[8].className = 't'; }, (r) => openRequest(r));
    pager($('#rq-pager'), d, A.rq);
  },

  releases({ releases: n, status: st }) {
    const rel = st.release;
    const current = n.releases.find((r) => cmpVersion(r.version, n.current) === 0) || n.releases[0];
    const older = n.releases.filter((r) => cmpVersion(r.version, n.current) < 0);
    const from = [...older.map((r) => [r.version, `v${r.version} — ${r.title}`]), ...n.legacy.map((l) => [l.id, `${l.name} (replaced app)`])];
    const latest = rel.latest || {};
    setHTML($('#releases'), html`<div class="rel-panel">
      <div class="card">
        <div class="rel-head"><h2>Versions</h2><button class="ghost small" type="button" data-act="check-release">${icon('retry')}Check for a newer release</button></div>
        <div class="rel-versions">
          <div class="rel-v"><span class="k">This app</span><b>v${rel.version}</b><small>${rel.revision ? `build ${rel.revision}` : 'no build revision'}${rel.builtAt ? ` · built ${fmtDate(String(rel.builtAt).slice(0, 10))}` : ''}</small></div>
          <div class="rel-v"><span class="k">Web manifest</span><b>${rel.manifest ? `v${rel.manifest}` : '—'}</b><small>${rel.manifestMatches === false ? html`${icon('warn')} differs from the app` : html`${icon('check')} matches the app`}</small></div>
          <div class="rel-v ${rel.updateAvailable ? 'is-update' : ''}"><span class="k">Latest release</span><b>v${latest.version}</b>
            <small>${rel.updateAvailable ? html`${icon('arrow')} update available` : html`${icon('check')} up to date`} · ${latest.source === 'feed' ? `release feed${latest.checkedAt ? `, checked ${when(String(latest.checkedAt).replace('T', ' ').slice(0, 19))}` : ''}` : "this build's own notes"}</small></div>
        </div>
        ${latest.error ? html`<p class="opt-note">${icon('info')} ${latest.error}</p>` : ''}
      </div>
      <div class="card">
        <div class="rel-head"><h2>v${current.version} · ${current.title}</h2>${compatBadge(current.compatibility?.level)}</div>
        <p class="muted small">${current.date ? fmtDate(current.date) : ''}</p>
        ${current.summary ? html`<p>${current.summary}</p>` : ''}
        ${changes(current.changes)}
      </div>
      ${n.newer?.length ? html`<div class="card rel-newer"><h2>${icon('arrow')}Newer releases</h2>${n.newer.map((r) => html`<div class="rel-mini"><div class="rel-head"><b>v${r.version} · ${r.title}</b>${compatBadge(r.compatibility?.level)}</div><p class="muted">${r.summary || ''}</p></div>`)}</div>` : ''}
      ${from.length ? html`<div class="card">
        <h2>Migration plan</h2>
        <label class="field rel-from"><span>From</span><select id="adm-from">${from.map(([v, l]) => html`<option value="${v}">${l}</option>`)}</select></label>
        <div id="adm-plan" aria-live="polite"></div>
      </div>` : ''}
      <div class="card">
        <h2>Compatibility of every earlier version with v${n.current}</h2>
        <div class="table-wrap"><table class="rel-matrix">
          <thead><tr><th scope="col">From</th><th scope="col">Compatibility</th><th scope="col">Steps</th><th scope="col">Recommendation</th></tr></thead>
          <tbody>${n.matrix.map((m) => html`<tr><th scope="row">${m.from.startsWith('legacy') ? m.name : `v${m.from} — ${m.name}`}</th><td>${compatBadge(m.level)}</td>
            <td class="num">${m.steps}${m.requiredSteps ? ` (${m.requiredSteps} required)` : ''}</td><td>${m.recommendation}</td></tr>`)}</tbody>
        </table></div>
      </div>
      <details class="card"><summary><b>All releases</b></summary>
        ${n.releases.map((r) => html`<div class="rel-mini"><div class="rel-head"><b>v${r.version} · ${r.title}</b>${compatBadge(r.compatibility?.level)}</div><p class="muted small">${r.date ? fmtDate(r.date) : ''}</p>${r.summary ? html`<p>${r.summary}</p>` : ''}${changes(r.changes)}</div>`)}
        ${n.legacy.map((l) => html`<div class="rel-mini"><b>${l.name}</b><p class="muted small">${l.stack}</p><p>${l.summary}</p></div>`)}
      </details>
    </div>`);
    if (from.length) showPlan(from[0][0]);
  },

  system({ status: s }) {
    const a = s.audit, w = a.writer || {}, mt = a.maintenance || {}, g = s.geoip, t = s.tools;
    const state = (ok, text) => html`<span class="compat ${ok === true ? 'ok' : ok === false ? 'err' : 'warn'}">${icon(ok === true ? 'check' : 'warn')}${text}</span>`;
    const tables = a.tables && !a.tables.error ? a.tables : null;
    const kv = (pairs) => html`<dl class="kv">${pairs.filter(([, v]) => v != null && v !== '').map(([k, v]) => html`<dt>${k}</dt><dd>${v}</dd>`)}</dl>`;
    setHTML($('#system'), html`<div class="sys-grid">
      <div class="card">
        <h2>Audit store ${state(a.enabled ? (w.state === 'ready' ? true : w.state === 'starting' ? null : false) : null, a.enabled ? w.state : 'off')}</h2>
        ${kv([
          ['Database', `${a.provider || 'Sqlite'} · Ark.Rapid.Database`],
          ['File', a.file],
          ['Size on disk', a.size != null ? fmt.bytes(a.size) : null],
          ['Schema version', a.schema],
          ['Requests table', tables ? `${fmt.full(tables.audit_request.rows)} rows${tables.audit_request.first ? ` · ${tables.audit_request.first} → ${tables.audit_request.last}` : ''}` : null],
          ['Downloads table', tables ? `${fmt.full(tables.audit_download.rows)} rows${tables.audit_download.first ? ` · ${tables.audit_download.first} → ${tables.audit_download.last}` : ''}` : null],
          ['Daily totals', tables ? `${fmt.full(tables.audit_daily.rows)} days${tables.audit_daily.first ? ` · ${tables.audit_daily.first} → ${tables.audit_daily.last}` : ''}` : null],
          ['Rows written', fmt.full(w.written)],
          ['Waiting to be written', fmt.full(w.queued)],
          ['Dropped (queue full)', fmt.full(w.dropped)],
          ['Failed to write', fmt.full(w.failed)],
          ['Last write', w.lastWriteAt ? new Date(w.lastWriteAt).toLocaleString() : 'never'],
          ['Last error', w.lastError ? `${w.lastError}${w.lastErrorAt ? ` (${new Date(w.lastErrorAt).toLocaleString()})` : ''}` : null],
          ['Daily totals updated', mt.lastRollupAt ? new Date(mt.lastRollupAt).toLocaleString() : 'not yet'],
          ['Retention', a.retentionDays > 0 ? `raw rows kept ${a.retentionDays} days${mt.lastPruneAt ? ` · last pruned ${new Date(mt.lastPruneAt).toLocaleString()} (${fmt.full(mt.lastPruned)} rows)` : ''}` : 'keep everything'],
          ['Maintenance error', mt.lastError],
        ])}
        <p><button class="ghost small" type="button" data-act="maintenance">${icon('retry')}Recompute daily totals now</button></p>
      </div>
      <div class="card">
        <h2>What is recorded</h2>
        ${kv([
          ['Requests', a.logRequests ? `every request${a.includeStaticFiles ? ', including static files' : ' except static files'}` : 'downloads only'],
          ['Never logged', (a.excludePaths || []).join(', ') || 'nothing excluded'],
          ['IP addresses', { Full: 'stored as received', Masked: 'stored shortened (last IPv4 octet / 80 IPv6 bits zeroed)', Hashed: 'stored as a salted hash' }[a.ipMode] || a.ipMode],
          ['Day boundaries', `${a.timeZone}${a.timeZoneNote ? ` — ${a.timeZoneNote}` : ''}`],
          ['Item metadata', a.captureItemMetadata ? 'yt-dlp writes genre, channel and duration for every downloaded item' : 'off (only what the page probed)'],
          ['Country from header', a.countryHeader || 'not used (GeoIP)'],
        ])}
      </div>
      <div class="card">
        <h2>IP geolocation ${state(g.state === 'ready' ? true : g.state === 'off' ? null : g.state === 'failed' || g.state === 'missing' ? false : null, g.state)}</h2>
        ${kv([
          ['Edition', g.edition ? `DB-IP ${g.edition} lite` : null],
          ['File', g.file],
          ['Month', g.month],
          ['Database', g.databaseType],
          ['Built', g.buildDate ? new Date(g.buildDate).toLocaleDateString() : null],
          ['Loaded', g.loadedAt ? new Date(g.loadedAt).toLocaleString() : null],
          ['Next check', g.nextCheck ? new Date(g.nextCheck).toLocaleString() : null],
          ['Error', g.error],
        ])}
        <p class="muted small">${g.attribution}</p>
      </div>
      <div class="card">
        <h2>Download engine</h2>
        ${kv([
          ['yt-dlp', t.ytDlp.ok ? t.ytDlp.version : `missing${t.ytDlp.note ? ` — ${t.ytDlp.note}` : ''}`],
          ['ffmpeg', t.ffmpeg.ok ? t.ffmpeg.version : `missing${t.ffmpeg.note ? ` — ${t.ffmpeg.note}` : ''}`],
          ['JS runtime', t.jsRuntime.ok ? `${t.jsRuntime.name} ${t.jsRuntime.version}` : `missing${t.jsRuntime.note ? ` — ${t.jsRuntime.note}` : ''}`],
          ['App', `ARK-RAPID v${s.release.version}${s.release.revision ? ` (${s.release.revision})` : ''}`],
          ['Server', `${s.server.machine} · ${s.server.os} · .NET ${s.server.dotnet}`],
          ['Server time', new Date(s.server.time).toLocaleString()],
        ])}
      </div>
    </div>`);
  },
};

function renderTiles(host, tiles) {
  host.replaceChildren();
  for (const t of tiles) {
    const el = document.createElement('div');
    host.append(el);
    statTile(el, t);
  }
}

function heatTable(host, cells, noun) {
  const days = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
  const list = [...(cells || [])].sort((a, z) => z.count - a.count);
  table(host, [{ key: 'w', label: 'Weekday' }, { key: 'h', label: 'Hour' }, { key: 'count', label: noun, num: true, format: fmt.full }],
    list.map((c) => ({ w: days[c.weekday], h: `${String(c.hour).padStart(2, '0')}:00`, count: c.count })), { empty: 'Nothing in this period.' });
}

function linkVideos(host, list) {
  const trs = host.querySelectorAll('tbody tr');
  list.forEach((v, i) => {
    const td = trs[i]?.cells[0];
    if (!td) return;
    td.className = 't';
    if (/^[A-Za-z0-9_-]{11}$/.test(v.videoId || '')) {
      const a = document.createElement('a');
      a.href = `https://www.youtube.com/watch?v=${v.videoId}`;
      a.target = '_blank';
      a.rel = 'noopener';
      a.textContent = v.title || v.videoId;
      td.replaceChildren(a);
    }
    td.title = v.title || '';
  });
}

let langNames = null;
function langName(tag) {
  if (!tag || tag === 'Unknown') return tag;
  try { langNames ||= new Intl.DisplayNames(undefined, { type: 'language' }); return `${langNames.of(tag)} (${tag})`; } catch { return tag; }
}

function stateBadge(status) {
  const s = document.createElement('span');
  s.className = `state ${status || ''}`;
  s.textContent = `${status === 'completed' ? '✓ ' : status === 'failed' ? '✕ ' : ''}${status || '?'}`;
  return s;
}

function decorateRows(host, list, decorate, open) {
  const trs = host.querySelectorAll('tbody tr');
  list.forEach((r, i) => {
    const tr = trs[i];
    if (!tr) return;
    tr.classList.add('clickable');
    tr.tabIndex = 0;
    decorate(tr, r);
    tr.addEventListener('click', (e) => { if (!e.target.closest('a')) open(r); });
    tr.addEventListener('keydown', (e) => { if (e.key === 'Enter') open(r); });
  });
}

function pager(host, d, state) {
  const first = d.total ? (d.page - 1) * d.size + 1 : 0;
  const last = Math.min(d.total, d.page * d.size);
  setHTML(host, html`<span>${d.total ? `${fmt.full(first)}–${fmt.full(last)} of ${fmt.full(d.total)}` : 'Nothing found'}</span>
    <span class="btns"><button class="ghost small" type="button" data-page="-1" ${d.page <= 1 ? 'disabled' : ''}>Newer</button>
    <button class="ghost small" type="button" data-page="1" ${last >= d.total ? 'disabled' : ''}>Older</button></span>`);
  host.onclick = (e) => {
    const b = e.target.closest('[data-page]');
    if (!b || b.disabled) return;
    state.page = Math.max(1, state.page + parseInt(b.dataset.page, 10));
    refresh(true);
  };
}

async function showPlan(from) {
  const box = $('#adm-plan');
  if (!box) return;
  box.classList.add('dim');
  try {
    setHTML(box, planHtml(await request('GET', `/api/releases/migration?from=${encodeURIComponent(from)}`)));
  } catch (err) {
    setHTML(box, html`<p class="opt-note">${icon('warn')} ${err.message}</p>`);
  }
  box.classList.remove('dim');
}

// ── drawer: one download / one request in full ──────────────────────────────
const GROUPS = [
  ['The video', ['title', 'url', 'video_id', 'list_id', 'channel', 'channel_id', 'channel_url', 'channel_followers', 'category', 'categories', 'genre', 'tags', 'video_language', 'duration_sec', 'view_count', 'like_count', 'upload_date', 'age_limit', 'live_status', 'is_short', 'is_music', 'artist', 'track', 'album', 'site', 'url_kind']],
  ['What was asked for', ['mode', 'format', 'scope', 'quality', 'codec', 'container', 'hdr', 'audio_format', 'audio_bitrate', 'clip_sections', 'clip_seconds', 'playlist', 'playlist_items', 'playlist_count', 'subtitles', 'subtitle_langs', 'sponsorblock', 'embed', 'file_name_style', 'label', 'estimated_bytes']],
  ['Outcome', ['status', 'error', 'error_detail', 'created_utc', 'started_utc', 'finished_utc', 'queue_ms', 'run_ms', 'file_count', 'total_bytes', 'item_count', 'served_count', 'served_bytes', 'first_served_utc', 'last_served_utc', 'app_version', 'ytdlp_version']],
  ['Who', ['ip', 'ip_country', 'ip_country_name', 'ip_region', 'ip_city', 'user_name', 'user_id', 'client_key', 'device', 'browser', 'browser_version', 'os', 'os_version', 'is_bot', 'language', 'accept_language', 'client_tz', 'client_screen', 'client_pwa', 'client_version', 'referrer', 'user_agent']],
];
const BYTES = new Set(['total_bytes', 'served_bytes', 'estimated_bytes', 'bytes']);
const MS = new Set(['queue_ms', 'run_ms', 'duration_ms']);
const FLAGS = new Set(['is_short', 'is_music', 'hdr', 'playlist', 'is_bot', 'client_pwa']);
function niceValue(k, v) {
  if (v == null || v === '') return null;
  if (BYTES.has(k)) return fmt.bytes(v);
  if (MS.has(k)) return fmt.ms(v);
  if (FLAGS.has(k)) return v ? 'yes' : 'no';
  if (k.endsWith('_utc')) return whenFull(v);
  if (k === 'duration_sec' || k === 'clip_seconds') return fmt.ms(v * 1000);
  if (k === 'view_count' || k === 'like_count' || k === 'channel_followers') return fmt.full(v);
  return String(v);
}
const LABELS = { url: 'URL', video_id: 'Video id', ytdlp_version: 'yt-dlp version', is_bot: 'Bot', client_pwa: 'Installed app', client_tz: 'Time zone',
  client_screen: 'Screen', client_version: 'Page version', client_key: 'Client key', ip: 'IP address', ts_utc: 'Time' };
const label = (k) => LABELS[k] || k.replace(/_(utc|ms|sec)$/, '').replace(/_/g, ' ').replace(/^ip /, 'IP ').replace(/^\w/, (c) => c.toUpperCase());

async function openDownload(jobId) {
  const d = $('#drawer');
  d.hidden = false;
  $('#drawer-title').textContent = 'Download';
  setHTML($('#drawer-body'), html`<p class="loading-note"><span class="spinner"></span><span>Loading…</span></p>`);
  let res;
  try {
    res = await request('GET', `/api/admin/downloads/${encodeURIComponent(jobId)}`);
  } catch (err) {
    setHTML($('#drawer-body'), html`<div class="banner err">${icon('warn')}<p class="grow">${err.message}</p></div>`);
    return;
  }
  const r = res.row;
  $('#drawer-title').textContent = r.title || r.job_id;
  const used = new Set();
  const groups = GROUPS.map(([title, keys]) => {
    const pairs = keys.map((k) => { used.add(k); return [label(k), niceValue(k, r[k])]; }).filter(([, v]) => v != null);
    return html`<section class="kv-group"><h3>${title}</h3><dl class="kv">${pairs.map(([k, v]) => html`<dt>${k}</dt><dd>${v}</dd>`)}</dl></section>`;
  });
  let files = [];
  let items = [];
  try { files = JSON.parse(r.files || '[]'); } catch { /* ignore */ }
  try { items = JSON.parse(r.items || '[]'); } catch { /* ignore */ }
  setHTML($('#drawer-body'), html`
    ${groups}
    ${files.length ? html`<section class="kv-group"><h3>Files (${files.length})</h3><div class="table-wrap"><table class="data-table"><thead><tr><th>Name</th><th>Kind</th><th class="num">Size</th></tr></thead>
      <tbody>${files.map((f) => html`<tr><td>${f.n}</td><td>${f.k}</td><td class="num">${fmt.bytes(f.s)}</td></tr>`)}</tbody></table></div></section>` : ''}
    ${items.length > 1 ? html`<section class="kv-group"><h3>Items (${items.length})</h3><div class="table-wrap"><table class="data-table"><thead><tr><th>#</th><th>Title</th><th>Genre</th><th class="num">Length</th></tr></thead>
      <tbody>${items.map((it) => html`<tr><td class="num">${it.i ?? ''}</td><td>${it.t || it.id || ''}</td><td>${it.cat || it.g || ''}</td><td class="num">${it.d ? fmt.ms(it.d * 1000) : ''}</td></tr>`)}</tbody></table></div></section>` : ''}
    ${res.requests?.length ? html`<section class="kv-group"><h3>Requests for this job (${res.requests.length})</h3><div class="table-wrap"><table class="data-table"><thead><tr><th>When</th><th>Request</th><th class="num">Status</th><th>Detail</th></tr></thead>
      <tbody>${res.requests.map((q) => html`<tr><td>${when(q.ts_utc)}</td><td>${q.method} ${q.path}</td><td class="num">${q.status}</td><td>${q.detail || ''}</td></tr>`)}</tbody></table></div></section>` : ''}
    <section class="kv-group"><h3>Job id</h3><p><code>${r.job_id}</code></p></section>`);
  $('#drawer-close').focus();
}

function openRequest(r) {
  $('#drawer').hidden = false;
  $('#drawer-title').textContent = `${r.method} ${r.path}`;
  const pairs = Object.entries(r).map(([k, v]) => [label(k), niceValue(k, v)]).filter(([, v]) => v != null);
  setHTML($('#drawer-body'), html`<section class="kv-group"><h3>Request</h3><dl class="kv">${pairs.map(([k, v]) => html`<dt>${k}</dt><dd>${v}</dd>`)}</dl></section>
    ${r.job_id ? html`<p><button class="ghost small" type="button" data-open-job="${r.job_id}">Open the download</button></p>` : ''}`);
  $('#drawer-close').focus();
}

// ── events ──────────────────────────────────────────────────────────────────
function bind() {
  $('#key-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const key = $('#key').value;
    try {
      await request('POST', '/api/admin/session', { key });
      $('#key').value = '';
      loadSession();
    } catch (err) {
      toast(err.message, 'err', 5000);
      $('#key').select();
    }
  });
  $('#btn-signout').addEventListener('click', async () => {
    try { await request('DELETE', '/api/admin/session'); } catch { /* ignore */ }
    A.cache = {};
    loadSession();
  });
  $('#btn-theme').addEventListener('click', () => {
    const cur = store.get('ydl.theme', 'auto');
    const next = THEMES[(THEMES.indexOf(cur) + 1) % THEMES.length];
    applyTheme(next);
    store.set('ydl.theme', next);
    redraw();
  });
  $('#range-seg').addEventListener('click', (e) => {
    const b = e.target.closest('[data-range]');
    if (!b) return;
    A.preset = b.dataset.range;
    if (A.preset === 'custom') { const r = currentRange(); A.from ||= r.from; A.to ||= r.to; }
    A.dl.page = 1; A.rq.page = 1;
    syncRangeUi();
    refresh(false);
  });
  for (const id of ['#from', '#to']) {
    $(id).addEventListener('change', () => {
      const f = $('#from').value, t = $('#to').value;
      if (!f || !t) return;
      A.from = f <= t ? f : t;
      A.to = f <= t ? t : f;
      A.dl.page = 1; A.rq.page = 1;
      syncRangeUi();
      refresh(false);
    });
  }
  $('#btn-refresh').addEventListener('click', () => { A.cache = {}; refresh(true); });
  $('.tabs').addEventListener('click', (e) => {
    const b = e.target.closest('[data-tab]');
    if (b) selectTab(b.dataset.tab);
  });
  $('.tabs').addEventListener('keydown', (e) => {
    if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
    const tabs = $$('.tabs [role="tab"]');
    const i = tabs.findIndex((t) => t.dataset.tab === A.tab);
    const next = tabs[(i + (e.key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length];
    next.focus();
    selectTab(next.dataset.tab);
  });
  for (const [formId, state] of [['#dl-filters', A.dl], ['#rq-filters', A.rq]]) {
    const form = $(formId);
    form.addEventListener('submit', (e) => {
      e.preventDefault();
      state.filters = Object.fromEntries(new FormData(form).entries());
      state.page = 1;
      refresh(true);
    });
    form.addEventListener('change', (e) => { if (e.target.tagName === 'SELECT') form.requestSubmit(); });
  }
  document.addEventListener('click', async (e) => {
    const exp = e.target.closest('[data-export]');
    if (exp) { triggerDownload(`/api/admin/export/${exp.dataset.export}?${rangeQuery()}`); return; }
    const act = e.target.closest('[data-act]');
    if (act?.dataset.act === 'check-release') {
      act.disabled = true;
      try { await request('POST', '/api/admin/release/check'); delete A.cache.status; toast('Release feed checked.', 'ok', 2500); refresh(true); }
      catch (err) { toast(err.message, 'err', 5000); }
      act.disabled = false;
    }
    if (act?.dataset.act === 'maintenance') {
      act.disabled = true;
      try { await request('POST', '/api/admin/maintenance'); A.cache = {}; toast('Daily totals recomputed.', 'ok', 2500); refresh(true); }
      catch (err) { toast(err.message, 'err', 5000); }
      act.disabled = false;
    }
    const job = e.target.closest('[data-open-job]');
    if (job) openDownload(job.dataset.openJob);
  });
  document.addEventListener('change', (e) => { if (e.target.id === 'adm-from') showPlan(e.target.value); });
  $('#drawer-close').addEventListener('click', closeDrawer);
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && !$('#drawer').hidden) closeDrawer(); });
  window.addEventListener('resize', debounce(redraw, 200));
  window.addEventListener('hashchange', () => { const before = `${A.tab}|${A.preset}`; readHash(); if (`${A.tab}|${A.preset}` !== before && !$('#console').hidden) { syncRangeUi(); selectTab(A.tab); } });
}

function closeDrawer() { $('#drawer').hidden = true; }

function redraw() {
  hideTip();
  const panel = $(`#p-${A.tab}`);
  for (const [name, run] of drawn) if (panel?.contains($(`[data-chart="${name}"]`))) run();
}

const THEME_COLOR = { light: '#f4f6f9', dark: '#081525' };
function applyTheme(t) {
  if (t === 'dark' || t === 'light') document.documentElement.dataset.theme = t;
  else delete document.documentElement.dataset.theme;
  for (const meta of $$('meta[name="theme-color"]')) {
    const scheme = (meta.getAttribute('media') || '').includes('dark') ? 'dark' : 'light';
    meta.content = THEME_COLOR[t === 'dark' || t === 'light' ? t : scheme];
  }
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
else init();
