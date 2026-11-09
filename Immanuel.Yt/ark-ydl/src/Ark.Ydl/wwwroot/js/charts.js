// Small, dependency-free charts for the admin console. One method everywhere: thin marks (bars <= 24px with a 4px
// rounded data-end, square at the baseline; 2px lines with a 10% area wash), a 2px surface gap between touching
// fills, hairline solid grid, one y-axis, a legend whenever there are two or more series, text in text tokens only,
// and tooltips that enhance but never gate (every chart has a table view with the same numbers).
// All labels are untrusted data (video titles, user agents): they only ever reach the DOM through textContent.

const NS = 'http://www.w3.org/2000/svg';

function svgEl(tag, attrs = {}, parent = null) {
  const e = document.createElementNS(NS, tag);
  for (const [k, v] of Object.entries(attrs)) if (v != null) e.setAttribute(k, String(v));
  if (parent) parent.append(e);
  return e;
}
function svgText(parent, x, y, s, cls, anchor = 'start') {
  const t = svgEl('text', { x, y, class: cls, 'text-anchor': anchor }, parent);
  t.textContent = s;
  return t;
}
function div(cls, parent = null, text = null) {
  const e = document.createElement('div');
  if (cls) e.className = cls;
  if (text != null) e.textContent = text;
  if (parent) parent.append(e);
  return e;
}

// ── number formats ──────────────────────────────────────────────────────────
const compact = new Intl.NumberFormat(undefined, { notation: 'compact', maximumFractionDigits: 1 });
const whole = new Intl.NumberFormat(undefined);
export const fmt = {
  count: (n) => (n == null ? '–' : Math.abs(n) < 10000 ? whole.format(Math.round(n)) : compact.format(n)),
  full: (n) => (n == null ? '–' : whole.format(Math.round(n))),
  bytes: (n) => {
    if (n == null || !isFinite(n)) return '–';
    if (n <= 0) return '0 B';
    const u = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
    let i = 0;
    while (n >= 1000 && i < u.length - 1) { n /= 1024; i++; }
    return `${n >= 100 || i === 0 ? Math.round(n) : n.toFixed(1).replace(/\.0$/, '')} ${u[i]}`;
  },
  pct: (x) => (x == null || !isFinite(x) ? '–' : `${(x * 100).toFixed(x > 0 && x < 0.1 ? 1 : 0)}%`),
  ms: (ms) => {
    if (ms == null || !isFinite(ms)) return '–';
    if (ms < 1000) return `${Math.round(ms)} ms`;
    const s = ms / 1000;
    if (s < 90) return `${s.toFixed(s < 10 ? 1 : 0)} s`;
    const m = Math.floor(s / 60);
    return m < 90 ? `${m} min ${Math.round(s % 60)} s` : `${Math.floor(m / 60)} h ${m % 60} min`;
  },
  secs: (s) => {
    if (s == null || !isFinite(s) || s <= 0) return '0 min';
    const h = Math.floor(s / 3600), m = Math.round((s % 3600) / 60);
    return h ? `${fmt.count(h)} h ${m} min` : `${m} min`;
  },
};

// ── the one tooltip ─────────────────────────────────────────────────────────
let tipEl = null;
function tip() {
  if (!tipEl) {
    tipEl = div('chart-tip', document.body);
    tipEl.setAttribute('role', 'tooltip');
    tipEl.hidden = true;
  }
  return tipEl;
}
/** rows: [{ value, label, color, key: 'line' | 'rect' }] — the value leads, the label follows. */
export function showTip(anchor, title, rows) {
  const t = tip();
  t.replaceChildren();
  if (title) div('tip-title', t, title);
  for (const r of rows) {
    const row = div('tip-row', t);
    const k = document.createElement('span');
    k.className = `tip-key ${r.key === 'rect' ? 'rect' : 'line'}`;
    if (r.color) k.style.background = r.color;
    else k.style.visibility = 'hidden';
    row.append(k);
    const v = document.createElement('b');
    v.textContent = r.value;
    row.append(v);
    if (r.label) {
      const l = document.createElement('span');
      l.textContent = r.label;
      row.append(l);
    }
  }
  t.hidden = false;
  const box = t.getBoundingClientRect();
  let x, y;
  if (anchor instanceof Event && 'clientX' in anchor) { x = anchor.clientX + 14; y = anchor.clientY + 14; }
  else {
    const r = anchor.getBoundingClientRect();
    x = r.left + r.width / 2 + 8; y = r.top - box.height - 8;
    if (y < 8) y = r.bottom + 8;
  }
  x = Math.min(x, window.innerWidth - box.width - 8);
  y = Math.min(y, window.innerHeight - box.height - 8);
  t.style.left = `${Math.max(8, x)}px`;
  t.style.top = `${Math.max(8, y)}px`;
}
export function hideTip() { if (tipEl) tipEl.hidden = true; }
function bindTip(node, getContent) {
  const show = (e) => { const [title, rows] = getContent(); showTip(e, title, rows); node.classList.add('is-hot'); };
  const hide = () => { hideTip(); node.classList.remove('is-hot'); };
  node.addEventListener('pointermove', show);
  node.addEventListener('pointerleave', hide);
  node.addEventListener('focus', () => show(node));
  node.addEventListener('blur', hide);
}

// ── axes ────────────────────────────────────────────────────────────────────
function niceStep(raw, integer) {
  if (raw <= 0) return 1;
  const exp = Math.floor(Math.log10(raw));
  const base = 10 ** exp;
  const m = raw / base;
  let step = (m <= 1 ? 1 : m <= 2 ? 2 : m <= 2.5 ? 2.5 : m <= 5 ? 5 : 10) * base;
  if (integer && step < 1) step = 1;
  return step;
}
function scaleTicks(max, integer = true, n = 4, base = 0) {
  if (!(max > 0)) return { top: integer ? 4 : 1, ticks: integer ? [0, 1, 2, 3, 4] : [0, 0.25, 0.5, 0.75, 1] };
  if (base > 1) {
    // Binary units (bytes): find round steps in the unit the labels will use (4 GB, 8 GB… not 4.7 GB).
    const unit = base ** Math.max(0, Math.floor(Math.log(max) / Math.log(base)));
    const s = scaleTicks(max / unit, false, n);
    return { top: s.top * unit, ticks: s.ticks.map((t) => t * unit) };
  }
  const step = niceStep(max / n, integer);
  const top = Math.ceil(max / step) * step;
  const ticks = [];
  for (let v = 0; v <= top + step / 2; v += step) ticks.push(v);
  return { top, ticks };
}
/** Path for a bar whose top two corners are rounded (the data end); the baseline end stays square. */
function topRounded(x, y, w, h, r = 4) {
  r = Math.max(0, Math.min(r, w / 2, h));
  return `M${x},${y + h}V${y + r}Q${x},${y} ${x + r},${y}H${x + w - r}Q${x + w},${y} ${x + w},${y + r}V${y + h}Z`;
}
function axisLabelIdx(n, maxLabels) {
  if (n <= maxLabels) return [...Array(n).keys()];
  const step = Math.ceil(n / maxLabels);
  const out = [];
  for (let i = 0; i < n; i += step) out.push(i);
  return out;
}

export function legend(host, items) {
  const lg = div('legend', host);
  for (const it of items) {
    const item = div('legend-item', lg);
    const sw = document.createElement('span');
    sw.className = `legend-key ${it.key || 'rect'}`;
    sw.style.background = it.color;
    item.append(sw);
    if (it.icon) {
      const ic = document.createElement('span');
      ic.className = 'legend-icon';
      ic.textContent = it.icon;
      ic.setAttribute('aria-hidden', 'true');
      item.append(ic);
    }
    const t = document.createElement('span');
    t.textContent = it.label;
    item.append(t);
  }
  return lg;
}

// ── column chart (optionally stacked) ───────────────────────────────────────
/**
 * labels: x labels (one per column); long: the label in tooltips; series: [{ label, color, values, icon }].
 * Stacked bottom → top in series order.
 */
export function columns(host, { labels, long = labels, series, format = fmt.count, integer = true, height = 220, maxLabels = 7 }) {
  host.replaceChildren();
  if (series.length > 1) legend(host, series.map((s) => ({ label: s.label, color: s.color, icon: s.icon })));
  const W = Math.max(280, host.clientWidth || 600);
  const m = { l: 46, r: 8, t: 12, b: 26 };
  const pw = W - m.l - m.r, ph = height - m.t - m.b;
  const n = labels.length;
  const totals = labels.map((_, i) => series.reduce((a, s) => a + (s.values[i] || 0), 0));
  const { top, ticks } = scaleTicks(Math.max(0, ...totals), integer, 4, format === fmt.bytes ? 1024 : 0);
  const svg = svgEl('svg', { width: W, height, viewBox: `0 0 ${W} ${height}`, class: 'chart-svg', role: 'img' }, host);
  svg.setAttribute('aria-label', `${series.map((s) => s.label).join(', ')} by ${labels.length > 31 ? 'period' : 'day'}; the table view lists every value.`);
  const y = (v) => m.t + ph - (v / top) * ph;
  for (const t of ticks) {
    svgEl('line', { x1: m.l, x2: W - m.r, y1: y(t), y2: y(t), class: t === 0 ? 'axis' : 'grid' }, svg);
    svgText(svg, m.l - 6, y(t) + 4, format(t), 'tick', 'end');
  }
  const band = pw / Math.max(1, n);
  const bw = Math.max(2, Math.min(24, band - 2));
  for (const i of axisLabelIdx(n, Math.min(maxLabels, Math.max(2, Math.floor(pw / 70))))) {
    svgText(svg, m.l + band * i + band / 2, height - 8, labels[i], 'tick', 'middle');
  }
  for (let i = 0; i < n; i++) {
    const x = m.l + band * i + (band - bw) / 2;
    const g = svgEl('g', { class: 'col' }, svg);
    let acc = 0;
    const present = series.map((s, si) => ({ s, si, v: s.values[i] || 0 })).filter((p) => p.v > 0);
    present.forEach((p, k) => {
      const y0 = y(acc), y1 = y(acc + p.v);
      acc += p.v;
      let h = y0 - y1 - (k > 0 ? 2 : 0); // 2px surface gap between stacked segments
      if (h <= 0) return;
      const isTop = k === present.length - 1;
      svgEl('path', { d: isTop ? topRounded(x, y1, bw, h) : `M${x},${y1}h${bw}v${h}h${-bw}Z`, fill: p.s.color, class: 'mark' }, g);
    });
    const hit = svgEl('rect', { x: m.l + band * i, y: m.t, width: band, height: ph, class: 'hit', tabindex: 0 }, svg);
    const parts = series.map((s) => `${format(s.values[i] || 0)} ${s.label.toLowerCase()}`).join(', ');
    hit.setAttribute('aria-label', `${long[i]}: ${parts}`);
    bindTip(hit, () => [long[i], [...series].reverse().map((s) => ({ value: format(s.values[i] || 0), label: s.label, color: s.color, key: 'rect' }))
      .concat(series.length > 1 ? [{ value: format(totals[i]), label: 'in all' }] : [])]);
    hit.addEventListener('pointerenter', () => g.classList.add('is-hot'));
    hit.addEventListener('pointerleave', () => g.classList.remove('is-hot'));
  }
}

// ── line chart (one series) ─────────────────────────────────────────────────
export function line(host, { labels, long = labels, values, label, color = 'var(--series-1)', format = fmt.count, integer = true, height = 200, maxLabels = 7 }) {
  host.replaceChildren();
  const W = Math.max(280, host.clientWidth || 600);
  const m = { l: 46, r: 40, t: 14, b: 26 };
  const pw = W - m.l - m.r, ph = height - m.t - m.b;
  const n = values.length;
  const { top, ticks } = scaleTicks(Math.max(0, ...values), integer);
  const svg = svgEl('svg', { width: W, height, viewBox: `0 0 ${W} ${height}`, class: 'chart-svg', role: 'img', tabindex: 0 }, host);
  svg.setAttribute('aria-label', `${label}; use the arrow keys to step through the points, or the table view.`);
  const x = (i) => m.l + (n <= 1 ? pw / 2 : (i / (n - 1)) * pw);
  const y = (v) => m.t + ph - (v / top) * ph;
  for (const t of ticks) {
    svgEl('line', { x1: m.l, x2: W - m.r, y1: y(t), y2: y(t), class: t === 0 ? 'axis' : 'grid' }, svg);
    svgText(svg, m.l - 6, y(t) + 4, format(t), 'tick', 'end');
  }
  for (const i of axisLabelIdx(n, Math.min(maxLabels, Math.max(2, Math.floor(pw / 70))))) svgText(svg, x(i), height - 8, labels[i], 'tick', 'middle');
  if (n > 0) {
    const pts = values.map((v, i) => `${x(i)},${y(v || 0)}`);
    svgEl('path', { d: `M${x(0)},${y(0)}L${pts.join('L')}L${x(n - 1)},${y(0)}Z`, fill: color, 'fill-opacity': 0.1, class: 'area' }, svg);
    svgEl('path', { d: `M${pts.join('L')}`, fill: 'none', stroke: color, 'stroke-width': 2, 'stroke-linejoin': 'round', 'stroke-linecap': 'round' }, svg);
    const last = n - 1;
    svgEl('circle', { cx: x(last), cy: y(values[last] || 0), r: 4, fill: color, class: 'ringed' }, svg);
    svgText(svg, x(last) + 8, y(values[last] || 0) + 4, format(values[last] || 0), 'end-label');
  }
  const cross = svgEl('line', { y1: m.t, y2: m.t + ph, class: 'crosshair', visibility: 'hidden' }, svg);
  const dot = svgEl('circle', { r: 4, fill: color, class: 'ringed', visibility: 'hidden' }, svg);
  let cur = -1;
  const place = (i, anchor) => {
    cur = Math.max(0, Math.min(n - 1, i));
    cross.setAttribute('x1', x(cur)); cross.setAttribute('x2', x(cur)); cross.setAttribute('visibility', 'visible');
    dot.setAttribute('cx', x(cur)); dot.setAttribute('cy', y(values[cur] || 0)); dot.setAttribute('visibility', 'visible');
    showTip(anchor, long[cur], [{ value: format(values[cur] || 0), label, color, key: 'line' }]);
  };
  const clear = () => { cross.setAttribute('visibility', 'hidden'); dot.setAttribute('visibility', 'hidden'); hideTip(); };
  svg.addEventListener('pointermove', (e) => {
    const r = svg.getBoundingClientRect();
    const px = ((e.clientX - r.left) / r.width) * W;
    place(n <= 1 ? 0 : Math.round(((px - m.l) / pw) * (n - 1)), e);
  });
  svg.addEventListener('pointerleave', clear);
  svg.addEventListener('blur', clear);
  svg.addEventListener('keydown', (e) => {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
    e.preventDefault();
    place(cur < 0 ? n - 1 : cur + (e.key === 'ArrowRight' ? 1 : -1), dot);
  });
}

// ── ranked horizontal bars (one hue; the title names the series) ───────────
/** rows: [{ label, value, sub?, title? }] largest first; the tail past `max` folds into "Other". */
export function hbars(host, { rows, format = fmt.count, color = 'var(--series-1)', max = 10, other = true, empty = 'Nothing in this period.' }) {
  host.replaceChildren();
  let list = rows.filter((r) => r.value > 0);
  if (!list.length) { div('chart-empty', host, empty); return; }
  if (other && list.length > max) {
    const rest = list.slice(max - 1);
    list = [...list.slice(0, max - 1), { label: `Other (${rest.length})`, value: rest.reduce((a, r) => a + r.value, 0), muted: true }];
  }
  const top = Math.max(...list.map((r) => r.value));
  const box = div('hbars', host);
  for (const r of list) {
    const row = div('hbar', box);
    row.tabIndex = 0;
    const name = div('hbar-label', row, r.label);
    name.title = r.title || r.label;
    const track = div('hbar-track', row);
    const bar = div('hbar-fill', track);
    bar.style.width = `${Math.max(0.6, (r.value / top) * 100)}%`;
    bar.style.background = r.muted ? 'var(--viz-neutral)' : color;
    div('hbar-value', row, format(r.value));
    row.setAttribute('aria-label', `${r.label}: ${format(r.value)}${r.sub ? `, ${r.sub}` : ''}`);
    bindTip(row, () => [r.title || r.label, [{ value: format(r.value), color: r.muted ? 'var(--viz-neutral)' : color, key: 'rect' }]
      .concat(r.sub ? [{ value: r.sub }] : [])]);
  }
}

// ── tall segmented bar: one whole, <= 6 parts ──────────────────────────────
export function segmented(host, { parts, colors, format = fmt.count, empty = 'Nothing in this period.' }) {
  host.replaceChildren();
  let list = parts.filter((p) => p.value > 0);
  const total = list.reduce((a, p) => a + p.value, 0);
  if (!total) { div('chart-empty', host, empty); return; }
  if (list.length > 6) {
    const rest = list.slice(5);
    list = [...list.slice(0, 5), { label: 'Other', value: rest.reduce((a, p) => a + p.value, 0), other: true }];
  }
  const bar = div('segbar', host);
  bar.setAttribute('role', 'img');
  bar.setAttribute('aria-label', list.map((p) => `${p.label} ${fmt.pct(p.value / total)}`).join(', '));
  const width = host.clientWidth || 600;
  list.forEach((p, i) => {
    const share = p.value / total;
    const c = p.other ? 'var(--viz-neutral)' : (p.color || colors[i % colors.length]);
    const seg = div('seg', bar);
    seg.style.flex = `${share} 1 0`;
    seg.style.background = c;
    seg.tabIndex = 0;
    const text = fmt.pct(share);
    if (share * width > text.length * 8 + 16) {
      const l = div('seg-label', seg, text);
      if (p.ink === 'dark' || (!p.other && /series-4/.test(c))) l.classList.add('ink-dark');
    }
    seg.setAttribute('aria-label', `${p.label}: ${format(p.value)} (${text})`);
    bindTip(seg, () => [p.label, [{ value: format(p.value), color: c, key: 'rect' }, { value: text, label: 'of the total' }]]);
  });
  legend(host, list.map((p, i) => ({ label: p.label, color: p.other ? 'var(--viz-neutral)' : (p.color || colors[i % colors.length]) })));
}

// ── hour × weekday heat map (sequential, one hue) ──────────────────────────
const DAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
const DOW = [1, 2, 3, 4, 5, 6, 0]; // .NET DayOfWeek numbers in Monday-first order
export function heatmap(host, { cells, noun = 'downloads', format = fmt.count }) {
  host.replaceChildren();
  const grid = new Map(cells.map((c) => [`${c.weekday}:${c.hour}`, c.count]));
  const max = Math.max(0, ...cells.map((c) => c.count));
  if (!max) { div('chart-empty', host, 'Nothing in this period.'); return; }
  const W = Math.max(300, host.clientWidth || 600);
  const m = { l: 36, t: 6, b: 22, r: 4 };
  const cw = (W - m.l - m.r) / 24, ch = Math.min(26, Math.max(14, cw * 0.9));
  const H = m.t + ch * 7 + m.b;
  const svg = svgEl('svg', { width: W, height: H, viewBox: `0 0 ${W} ${H}`, class: 'chart-svg', role: 'img' }, host);
  svg.setAttribute('aria-label', `${noun} by weekday and hour; the table view lists every cell.`);
  const steps = 7;
  DOW.forEach((dow, r) => {
    svgText(svg, m.l - 6, m.t + r * ch + ch / 2 + 4, DAYS[r], 'tick', 'end');
    for (let h = 0; h < 24; h++) {
      const v = grid.get(`${dow}:${h}`) || 0;
      const bin = v === 0 ? 0 : 1 + Math.min(steps - 1, Math.floor((v / max) * steps * 0.999));
      const cell = svgEl('rect', {
        x: m.l + h * cw + 1, y: m.t + r * ch + 1, width: Math.max(1, cw - 2), height: Math.max(1, ch - 2), rx: 3,
        class: `heat h${bin}`, tabindex: 0,
      }, svg);
      const label = `${DAYS[r]} ${String(h).padStart(2, '0')}:00–${String((h + 1) % 24).padStart(2, '0')}:00`;
      cell.setAttribute('aria-label', `${label}: ${format(v)} ${noun}`);
      bindTip(cell, () => [label, [{ value: format(v), label: noun }]]);
    }
  });
  for (let h = 0; h < 24; h += 3) svgText(svg, m.l + h * cw + cw / 2, H - 6, `${String(h).padStart(2, '0')}h`, 'tick', 'middle');
  const scale = div('heat-scale', host);
  const lo = document.createElement('span'); lo.textContent = 'Fewer'; scale.append(lo);
  for (let b = 1; b <= steps; b++) { const s = document.createElement('i'); s.className = `heat-sw h${b}`; scale.append(s); }
  const hi = document.createElement('span'); hi.textContent = `More (max ${format(max)})`; scale.append(hi);
}

// ── stat tile ───────────────────────────────────────────────────────────────
/** delta: fraction change (0.12 = +12%) or null; upIsGood picks the colour of the direction. */
export function statTile(host, { label, value, delta = null, deltaText = null, upIsGood = true, spark = null, note = null }) {
  host.replaceChildren();
  host.classList.add('stat');
  div('stat-label', host, label);
  const row = div('stat-row', host);
  div('stat-value', row, value);
  if (delta != null && isFinite(delta)) {
    const up = delta > 0, flat = Math.abs(delta) < 0.005;
    const d = div(`stat-delta ${flat ? 'flat' : (up === upIsGood ? 'good' : 'bad')}`, host,
      `${flat ? '→' : up ? '▲' : '▼'} ${deltaText ?? fmt.pct(Math.abs(delta))} vs previous period`);
    d.title = 'Compared with the same number of days just before this range';
  } else if (note) {
    div('stat-delta flat', host, note);
  }
  if (spark && spark.length > 1) {
    const W = 84, H = 26, max = Math.max(1, ...spark);
    const svg = svgEl('svg', { width: W, height: H, viewBox: `0 0 ${W} ${H}`, preserveAspectRatio: 'none', class: 'spark', 'aria-hidden': 'true' }, row);
    const x = (i) => 2 + (i / (spark.length - 1)) * (W - 8);
    const y = (v) => H - 3 - (v / max) * (H - 8);
    svgEl('path', { d: `M${spark.map((v, i) => `${x(i)},${y(v)}`).join('L')}`, fill: 'none', class: 'spark-line' }, svg);
    svgEl('circle', { cx: x(spark.length - 1), cy: y(spark[spark.length - 1]), r: 3, class: 'spark-dot' }, svg);
  }
}

// ── table view (the accessible twin of every chart) ─────────────────────────
/** columns: [{ key, label, num?, format? }] */
export function table(host, columns, rows, { caption = null, empty = 'No rows.' } = {}) {
  host.replaceChildren();
  if (!rows.length) { div('chart-empty', host, empty); return; }
  const wrap = div('table-wrap', host);
  const t = document.createElement('table');
  t.className = 'data-table';
  if (caption) { const c = document.createElement('caption'); c.textContent = caption; t.append(c); }
  const thead = t.createTHead().insertRow();
  for (const c of columns) {
    const th = document.createElement('th');
    th.scope = 'col';
    th.textContent = c.label;
    if (c.num) th.className = 'num';
    thead.append(th);
  }
  const tb = t.createTBody();
  for (const r of rows) {
    const tr = tb.insertRow();
    for (const c of columns) {
      const td = tr.insertCell();
      const v = typeof c.value === 'function' ? c.value(r) : r[c.key];
      td.textContent = c.format ? c.format(v, r) : v ?? '';
      if (c.num) td.className = 'num';
    }
  }
  wrap.append(t);
}
