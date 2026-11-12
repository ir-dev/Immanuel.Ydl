// Dependency-free SVG charts for the report and the admin console, following the data-viz method:
// thin marks (bars <= 24px with 4px rounded data-ends, 2px lines with a 10% area wash), a 2px surface gap between
// touching fills, hairline solid grid, one y-axis, legends for >= 2 series, text in text tokens only, tooltips that
// enhance but never gate (every chart has a table twin). Sentiment is a diverging encoding: positive = teal-green,
// negative = warm red, neutral = gray (CSS tokens --pos / --neu / --neg). All labels reach the DOM via textContent.
const NS = 'http://www.w3.org/2000/svg';
const SERIES = ['var(--s1)', 'var(--s2)', 'var(--s3)', 'var(--s4)', 'var(--s5)', 'var(--s6)', 'var(--s7)', 'var(--s8)'];

function el(tag, attrs = {}, parent = null) {
  const e = document.createElementNS(NS, tag);
  for (const [k, v] of Object.entries(attrs)) if (v != null) e.setAttribute(k, String(v));
  if (parent) parent.append(e);
  return e;
}
function text(parent, x, y, s, cls = '', anchor = 'start') {
  const t = el('text', { x, y, class: cls, 'text-anchor': anchor }, parent);
  t.textContent = s;
  return t;
}
function div(cls, parent = null, content = null) {
  const d = document.createElement('div');
  if (cls) d.className = cls;
  if (content != null) d.textContent = content;
  if (parent) parent.append(d);
  return d;
}
const compact = new Intl.NumberFormat(undefined, { notation: 'compact', maximumFractionDigits: 1 });
const num = (n) => (Math.abs(n) < 10000 ? new Intl.NumberFormat().format(Math.round(n)) : compact.format(n));

// ── tooltip ──────────────────────────────────────────────────────────────
let tipEl = null;
function tip() {
  if (!tipEl) { tipEl = div('chart-tip', document.body); tipEl.setAttribute('role', 'tooltip'); tipEl.hidden = true; }
  return tipEl;
}
export function showTip(e, title, rows) {
  const t = tip();
  t.replaceChildren();
  if (title) div('tip-title', t, title);
  for (const r of rows) {
    const row = div('tip-row', t);
    const k = document.createElement('span');
    k.className = 'tip-key';
    if (r.color) k.style.background = r.color; else k.style.visibility = 'hidden';
    row.append(k);
    const b = document.createElement('b');
    b.textContent = r.value;
    row.append(b);
    if (r.label) { const l = document.createElement('span'); l.textContent = r.label; row.append(l); }
  }
  t.hidden = false;
  const box = t.getBoundingClientRect();
  let x = e.clientX + 14, y = e.clientY + 14;
  x = Math.min(x, window.innerWidth - box.width - 8);
  y = Math.min(y, window.innerHeight - box.height - 8);
  t.style.left = `${Math.max(8, x)}px`;
  t.style.top = `${Math.max(8, y)}px`;
}
export function hideTip() { if (tipEl) tipEl.hidden = true; }
function bindTip(node, content) {
  node.addEventListener('pointermove', (e) => { const [title, rows] = content(); showTip(e, title, rows); });
  node.addEventListener('pointerleave', hideTip);
  node.setAttribute('tabindex', '0');
  node.addEventListener('focus', () => { const r = node.getBoundingClientRect(); const [title, rows] = content(); showTip({ clientX: r.left + r.width / 2, clientY: r.top }, title, rows); });
  node.addEventListener('blur', hideTip);
}

function niceStep(raw) {
  if (raw <= 0) return 1;
  const exp = Math.floor(Math.log10(raw));
  const base = 10 ** exp;
  const m = raw / base;
  return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 2.5 ? 2.5 : m <= 5 ? 5 : 10) * base;
}

/** A table twin for any chart: rows of [label, ...values], headers given. */
export function tableTwin(parent, headers, rows) {
  const wrap = div('table-wrap chart-table', parent);
  wrap.hidden = true;
  const table = document.createElement('table');
  table.className = 'data';
  const thead = table.createTHead().insertRow();
  headers.forEach((h, i) => { const th = document.createElement('th'); th.textContent = h; if (i > 0) th.className = 'num'; thead.append(th); });
  const tb = table.createTBody();
  for (const r of rows) {
    const tr = tb.insertRow();
    r.forEach((v, i) => { const td = tr.insertCell(); td.textContent = typeof v === 'number' ? num(v) : v ?? ''; if (i > 0) td.className = 'num'; });
  }
  wrap.append(table);
  return wrap;
}
export function tableToggle(parent, twin) {
  const b = document.createElement('button');
  b.className = 'ghost small chart-toggle';
  b.type = 'button';
  b.textContent = 'Table';
  b.setAttribute('aria-pressed', 'false');
  b.addEventListener('click', () => { twin.hidden = !twin.hidden; b.setAttribute('aria-pressed', String(!twin.hidden)); b.textContent = twin.hidden ? 'Table' : 'Chart only'; });
  parent.append(b);
  return b;
}

/**
 * Stacked columns of positive / neutral / negative over time buckets, with the net score as a 2px line on the same 0..100% axis
 * (score is shown in the tooltip, not on a second axis). buckets: [{label, positive, neutral, negative, count, score}]
 */
export function sentimentTimeline(parent, buckets) {
  const W = 720, H = 220, L = 36, R = 12, T = 12, B = 34;
  const svg = el('svg', { viewBox: `0 0 ${W} ${H}`, class: 'chart', role: 'img', 'aria-label': 'Share of positive, neutral and negative comments over time' }, parent);
  const n = buckets.length;
  const pw = W - L - R, ph = H - T - B;
  for (let i = 0; i <= 4; i++) {
    const y = T + ph - (ph * i) / 4;
    el('line', { x1: L, x2: W - R, y1: y, y2: y, class: 'grid-line' }, svg);
    text(svg, L - 6, y + 4, `${i * 25}%`, '', 'end');
  }
  const slot = pw / Math.max(1, n);
  const bw = Math.min(24, slot * 0.7);
  const every = Math.ceil(n / Math.max(1, Math.floor(pw / 64)));
  let lastLabel = -every;
  buckets.forEach((b, i) => {
    const x = L + slot * i + (slot - bw) / 2;
    const total = Math.max(1, b.count || (b.positive + b.neutral + b.negative));
    const parts = [['pos', b.positive], ['neu', b.neutral], ['neg', b.negative]];
    let y = T + ph;
    const g = el('g', {}, svg);
    for (const [cls, v] of parts) {
      const h = (ph * v) / total;
      if (h <= 0) continue;
      const gap = y < T + ph ? 2 : 0;
      el('rect', { x, y: y - h, width: bw, height: Math.max(0, h - gap), class: cls, rx: 0 }, g);
      y -= h;
    }
    const hit = el('rect', { x: L + slot * i, y: T, width: slot, height: ph, class: 'hit' }, svg);
    bindTip(hit, () => [b.label, [
      { value: num(b.count), label: 'comments' },
      { value: `${Math.round((100 * b.positive) / total)}%`, label: 'positive', color: 'var(--pos)' },
      { value: `${Math.round((100 * b.neutral) / total)}%`, label: 'neutral', color: 'var(--neu)' },
      { value: `${Math.round((100 * b.negative) / total)}%`, label: 'negative', color: 'var(--neg)' },
      ...(b.score != null ? [{ value: `${b.score > 0 ? '+' : ''}${Math.round(b.score)}`, label: 'net score' }] : []),
    ]]);
    if (i - lastLabel >= every || (i === n - 1 && i - lastLabel >= every / 2)) { text(svg, x + bw / 2, H - 10, b.label, '', 'middle'); lastLabel = i; }
  });
  el('line', { x1: L, x2: W - R, y1: T + ph, y2: T + ph, class: 'axis' }, svg);
  const legend = div('legend', parent);
  for (const [cls, name] of [['pos', 'Positive'], ['neu', 'Neutral'], ['neg', 'Negative']]) { const s = document.createElement('span'); s.className = cls; s.append(document.createElement('i')); s.append(name); legend.append(s); }
  return svg;
}

/** Horizontal stacked share bars (languages, themes): rows [{name, count, positive, neutral, negative, score}]. */
export function shareBars(parent, rows, { total, max = 12 } = {}) {
  const box = div('hbars', parent);
  for (const r of rows.slice(0, max)) {
    const row = div('hbar', box);
    const name = div('name', row, r.name);
    name.title = r.name;
    const track = div('track', row);
    track.setAttribute('role', 'img');
    const n = Math.max(1, r.positive + r.neutral + r.negative || r.count);
    const share = total ? r.count / total : 1;
    track.style.width = `${Math.max(4, Math.round(share * 100))}%`;
    for (const [cls, v] of [['pos', r.positive], ['neu', r.neutral], ['neg', r.negative]]) {
      if (!v) continue;
      const i = document.createElement('i');
      i.className = cls;
      i.style.width = `${(100 * v) / n}%`;
      track.append(i);
    }
    track.setAttribute('aria-label', `${r.name}: ${num(r.count)} comments, ${Math.round((100 * r.positive) / n)}% positive, ${Math.round((100 * r.negative) / n)}% negative`);
    bindTip(track, () => [r.name, [
      { value: num(r.count), label: total ? `comments (${Math.round(share * 100)}%)` : 'comments' },
      { value: `${Math.round((100 * r.positive) / n)}%`, label: 'positive', color: 'var(--pos)' },
      { value: `${Math.round((100 * r.neutral) / n)}%`, label: 'neutral', color: 'var(--neu)' },
      { value: `${Math.round((100 * r.negative) / n)}%`, label: 'negative', color: 'var(--neg)' },
      ...(r.score != null ? [{ value: `${r.score > 0 ? '+' : ''}${Math.round(r.score)}`, label: 'net score' }] : []),
    ]]);
    const numEl = div('num', row, `${num(r.count)}${r.score != null ? ` · ${r.score > 0 ? '+' : ''}${Math.round(r.score)}` : ''}`);
    numEl.title = 'comments · net score';
  }
  return box;
}

/** Plain ranked bars, one hue: rows [{name, count}] (keywords, emotions, admin breakdowns). */
export function rankedBars(parent, rows, { max = 12, color = 'var(--accent)', format = num } = {}) {
  const box = div('hbars', parent);
  const top = Math.max(1, ...rows.slice(0, max).map((r) => r.count));
  for (const r of rows.slice(0, max)) {
    const row = div('hbar', box);
    const name = div('name', row, r.name);
    name.title = r.name;
    const track = div('track', row);
    const i = document.createElement('i');
    i.className = 'one';
    i.style.width = `${Math.max(2, (100 * r.count) / top)}%`;
    i.style.background = color;
    track.append(i);
    bindTip(track, () => [r.name, [{ value: format(r.count), label: r.label || '', color }]]);
    div('num', row, format(r.count));
  }
  return box;
}

/** Five age bands as columns (share of the audience, 0..100), the leading band in the accent colour, the rest in a muted tint. */
export function ageBands(parent, bands, primary) {
  const W = 360, H = 150, L = 8, R = 8, T = 22, B = 34;
  const svg = el('svg', { viewBox: `0 0 ${W} ${H}`, class: 'chart age-chart', role: 'img', 'aria-label': 'Estimated share of the audience by age band' }, parent);
  const pw = W - L - R, ph = H - T - B;
  const n = bands.length;
  const slot = pw / Math.max(1, n);
  const bw = Math.min(56, slot * 0.62);
  const max = Math.max(1, ...bands.map((b) => b.pct));
  el('line', { x1: L, x2: W - R, y1: T + ph, y2: T + ph, class: 'axis' }, svg);
  bands.forEach((b, i) => {
    const x = L + slot * i + (slot - bw) / 2;
    const h = (ph * b.pct) / max;
    const r = el('rect', { x, y: T + ph - h, width: bw, height: Math.max(0, h), rx: 4, class: b.key === primary ? 'lead' : 'rest' }, svg);
    text(svg, x + bw / 2, T + ph - h - 6, `${Math.round(b.pct)}%`, b.key === primary ? 'val' : '', 'middle');
    text(svg, x + bw / 2, H - 20, b.range, '', 'middle');
    text(svg, x + bw / 2, H - 7, b.label.split(' ')[0], 'faint', 'middle');
    bindTip(r, () => [`${b.label} (${b.range})`, [{ value: `${b.pct}%`, label: 'of the audience, estimated', color: b.key === primary ? 'var(--accent)' : 'var(--neu)' }]]);
  });
  return svg;
}

/** Multi-series line chart with one y-axis: series [{name, values:[...]}], labels [...]. Used by the admin console. */
export function lines(parent, labels, series, { height = 220, format = num, integer = true } = {}) {
  const W = 720, H = height, L = 44, R = 12, T = 14, B = 30;
  const svg = el('svg', { viewBox: `0 0 ${W} ${H}`, class: 'chart', role: 'img' }, parent);
  const pw = W - L - R, ph = H - T - B;
  const maxV = Math.max(1, ...series.flatMap((s) => s.values));
  let step = niceStep(maxV / 4);
  if (integer && step < 1) step = 1;
  const top = Math.ceil(maxV / step) * step;
  for (let v = 0; v <= top; v += step) {
    const y = T + ph - (ph * v) / top;
    el('line', { x1: L, x2: W - R, y1: y, y2: y, class: 'grid-line' }, svg);
    text(svg, L - 6, y + 4, format(v), '', 'end');
  }
  const n = labels.length;
  const x = (i) => L + (n <= 1 ? pw / 2 : (pw * i) / (n - 1));
  const y = (v) => T + ph - (ph * v) / top;
  series.forEach((s, si) => {
    const color = SERIES[si % SERIES.length];
    if (si === 0 && series.length === 1) {
      const d = `M${x(0)},${y(s.values[0] || 0)} ` + s.values.map((v, i) => `L${x(i)},${y(v || 0)}`).join(' ') + ` L${x(n - 1)},${T + ph} L${x(0)},${T + ph} Z`;
      el('path', { d, class: 'area', style: `fill:${color}` }, svg);
    }
    const d = s.values.map((v, i) => `${i ? 'L' : 'M'}${x(i)},${y(v || 0)}`).join(' ');
    el('path', { d, class: 'line', style: `stroke:${color}` }, svg);
    if (n <= 60) s.values.forEach((v, i) => el('circle', { cx: x(i), cy: y(v || 0), r: 3, class: 'dot', style: `fill:${color}` }, svg));
  });
  const every = Math.ceil(n / Math.max(1, Math.floor(pw / 70)));
  let lastLabel = -every;
  for (let i = 0; i < n; i++) {
    const hit = el('rect', { x: x(i) - (pw / Math.max(1, n - 1)) / 2, y: T, width: pw / Math.max(1, n - 1), height: ph, class: 'hit' }, svg);
    bindTip(hit, () => [labels[i], series.map((s, si) => ({ value: format(s.values[i] || 0), label: s.name, color: SERIES[si % SERIES.length] }))]);
    if (i - lastLabel >= every || (i === n - 1 && i - lastLabel >= every / 2)) { text(svg, x(i), H - 8, labels[i], '', i === 0 ? 'start' : i === n - 1 ? 'end' : 'middle'); lastLabel = i; }
  }
  el('line', { x1: L, x2: W - R, y1: T + ph, y2: T + ph, class: 'axis' }, svg);
  if (series.length > 1) {
    const legend = div('legend', parent);
    series.forEach((s, si) => { const sp = document.createElement('span'); const i = document.createElement('i'); i.style.background = SERIES[si % SERIES.length]; sp.append(i, s.name); legend.append(sp); });
  }
  return svg;
}

/** Weekday × hour heat strip (sequential, one hue). cells [{weekday, hour, count}]. */
export function heatmap(parent, cells, { color = 'var(--accent)' } = {}) {
  const W = 720, H = 160, L = 34, T = 18;
  const svg = el('svg', { viewBox: `0 0 ${W} ${H}`, class: 'chart', role: 'img', 'aria-label': 'Activity by weekday and hour' }, parent);
  const cw = (W - L - 8) / 24, ch = (H - T - 6) / 7;
  const max = Math.max(1, ...cells.map((c) => c.count));
  const days = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
  const map = new Map(cells.map((c) => [`${c.weekday}-${c.hour}`, c.count]));
  for (let d = 0; d < 7; d++) {
    text(svg, L - 6, T + d * ch + ch / 2 + 4, days[d], '', 'end');
    for (let h = 0; h < 24; h++) {
      const v = map.get(`${d}-${h}`) || 0;
      const r = el('rect', { x: L + h * cw + 1, y: T + d * ch + 1, width: cw - 2, height: ch - 2, rx: 2, style: `fill:${color};opacity:${v ? 0.15 + 0.85 * (v / max) : 0.06}` }, svg);
      bindTip(r, () => [`${days[d]} ${String(h).padStart(2, '0')}:00`, [{ value: num(v), label: 'events', color }]]);
    }
  }
  for (let h = 0; h < 24; h += 3) text(svg, L + h * cw + cw / 2, 11, `${h}h`, '', 'middle');
  return svg;
}
