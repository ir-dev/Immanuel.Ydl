// Admin console: sign in, then overview / analyses / feedback / requests / storage / system, all from /api/admin.
import { $, $$, html, setHTML, fmt, verdictName, toast, initTheme, debounce } from './util.js';
import { api } from './api.js';
import { lines, rankedBars, heatmap } from './charts.js';

initTheme();
const state = { session: null, from: null, to: null, tab: 'overview', page: 1, filters: {} };
boot();

async function boot() {
  try {
    state.session = await api.admin.session();
  } catch (e) { toast(e.message); return; }
  const s = state.session;
  if (!s.signedIn) {
    $('#signin').hidden = false;
    $('#signin-note').textContent = s.keyConfigured ? 'Enter the admin key to continue.' : s.keyProblem || (s.roleSignIn ? `Sign in with an account that has one of these roles: ${s.roles.join(', ')}.` : 'No admin key is configured (Feedback:Admin:Key). In Development, open this page from the server itself.');
    $('#key-form').addEventListener('submit', async (e) => {
      e.preventDefault();
      try { await api.admin.signIn($('#key').value); location.reload(); }
      catch (err) { $('#signin-error').hidden = false; $('#signin-error').textContent = err.message; }
    });
    return;
  }
  $('#console').hidden = false;
  $('#who').hidden = false;
  $('#who').textContent = s.method === 'key' ? 'Signed in with key' : s.method === 'development' ? 'Development (localhost)' : s.name || 'Signed in';
  if (s.method === 'key') { $('#btn-signout').hidden = false; $('#btn-signout').addEventListener('click', async () => { await api.admin.signOut(); location.reload(); }); }
  setRange(30);
  $$('.range-presets button').forEach((b) => b.addEventListener('click', () => { $$('.range-presets button').forEach((x) => x.classList.toggle('is-on', x === b)); setRange(b.dataset.days); state.page = 1; render(); }));
  $('#from').addEventListener('change', () => { state.from = $('#from').value; $$('.range-presets button').forEach((x) => x.classList.remove('is-on')); state.page = 1; render(); });
  $('#to').addEventListener('change', () => { state.to = $('#to').value; $$('.range-presets button').forEach((x) => x.classList.remove('is-on')); state.page = 1; render(); });
  $$('.tabs [role=tab]').forEach((b) => b.addEventListener('click', () => { state.tab = b.dataset.tab; state.page = 1; $$('.tabs [role=tab]').forEach((x) => x.setAttribute('aria-selected', String(x === b))); location.hash = state.tab; render(); }));
  if (location.hash) { const t = location.hash.slice(1); if ($(`#tab-${t}`)) { state.tab = t; $$('.tabs [role=tab]').forEach((x) => x.setAttribute('aria-selected', String(x.dataset.tab === t))); } }
  render();
}

function setRange(days) {
  const today = state.session.today;
  const t = new Date(today + 'T00:00:00Z');
  let f;
  if (days === 'mtd') f = new Date(Date.UTC(t.getUTCFullYear(), t.getUTCMonth(), 1));
  else { f = new Date(t); f.setUTCDate(f.getUTCDate() - (Number(days) - 1)); }
  state.from = f.toISOString().slice(0, 10);
  state.to = today;
  $('#from').value = state.from;
  $('#to').value = state.to;
}
const rangeQuery = () => `from=${state.from}&to=${state.to}`;

function render() {
  $$('.tab').forEach((t) => (t.hidden = t.id !== `tab-${state.tab}`));
  $('#range-row').hidden = state.tab === 'store' || state.tab === 'system';
  ({ overview: renderOverview, analyses: renderAnalyses, feedback: renderFeedback, requests: renderRequests, store: renderStore, system: renderSystem })[state.tab]();
}

function kpi(label, value, prev, { good = 'up', format = fmt.count } = {}) {
  let delta = '';
  if (prev != null && isFinite(prev) && prev !== 0 && value != null) {
    const d = ((value - prev) / prev) * 100;
    const cls = Math.abs(d) < 1 ? '' : (d > 0) === (good === 'up') ? 'up' : 'down';
    delta = html`<div class="delta ${cls}">${d > 0 ? '+' : ''}${d.toFixed(0)}% vs previous</div>`;
  } else if (prev === 0 && value > 0) delta = html`<div class="delta up">new</div>`;
  return html`<div class="kpi"><div class="label">${label}</div><div class="value">${format(value)}</div>${delta}</div>`;
}

async function renderOverview() {
  const box = $('#tab-overview');
  setHTML(box, html`<p class="muted">Loading…</p>`);
  try {
    const [o, b] = await Promise.all([api.admin.overview(rangeQuery()), api.admin.breakdowns(rangeQuery())]);
    const k = o.kpis, p = o.previousKpis;
    setHTML(box, html`
      <div class="kpis">
        ${kpi('Analyses', k.analyses, p.analyses)}
        ${kpi('Completed', k.completed, p.completed)}
        ${kpi('Failed', k.failed, p.failed, { good: 'down' })}
        ${kpi('Comments scored', k.comments, p.comments)}
        ${kpi('Videos', k.videos, p.videos)}
        ${kpi('People who asked', k.askers, p.askers)}
        ${kpi('Visitors', k.visitors, p.visitors)}
        ${kpi('Page views', k.pageViews, p.pageViews)}
        ${kpi('Liked the report', k.liked, p.liked)}
        ${kpi('Disliked', k.disliked, p.disliked, { good: 'down' })}
        ${kpi('Avg run time', k.avgRunMs, p.avgRunMs, { good: 'down', format: fmt.ms })}
        ${kpi('Avg video score', k.avgScore, null, { format: (v) => fmt.signed(v) })}
      </div>
      <div class="grid two">
        <section class="card"><div class="card-head"><h3>Analyses per day</h3></div><div class="slot" id="ov-daily"></div></section>
        <section class="card"><div class="card-head"><h3>Visitors and page views</h3></div><div class="slot" id="ov-visitors"></div></section>
      </div>
      <div class="grid two">
        <section class="card"><div class="card-head"><h3>Verdicts of analysed videos</h3></div><div id="ov-verdicts"></div></section>
        <section class="card"><div class="card-head"><h3>Comment sentiment (all scored comments)</h3></div><div id="ov-sentiment"></div></section>
        <section class="card"><div class="card-head"><h3>Comment languages</h3></div><div id="ov-langs"></div></section>
        <section class="card"><div class="card-head"><h3>Where the askers are</h3></div><div id="ov-countries"></div></section>
        <section class="card"><div class="card-head"><h3>Profiles (avg run time)</h3></div><div id="ov-profiles"></div></section>
        <section class="card"><div class="card-head"><h3>Devices</h3></div><div id="ov-devices"></div></section>
      </div>
      <section class="card"><div class="card-head"><h3>Most analysed videos</h3></div><div class="table-wrap"><table class="data"><thead><tr><th>Video</th><th>Channel</th><th class="num">Analyses</th><th class="num">Askers</th><th class="num">Score</th><th>Verdict</th></tr></thead>
        <tbody>${b.data.videos.length ? b.data.videos.map((v) => html`<tr><td class="clip"><a href="https://www.youtube.com/watch?v=${v.videoId}" target="_blank" rel="noopener">${v.title || v.videoId}</a></td><td>${v.channel || ''}</td><td class="num">${v.count}</td><td class="num">${v.askers}</td><td class="num">${fmt.signed(v.score)}</td><td>${verdictName(v.verdict)}</td></tr>`) : html`<tr><td colspan="6" class="muted">Nothing analysed in this range.</td></tr>`}</tbody></table></div></section>
      <section class="card"><div class="card-head"><h3>When analyses are requested</h3></div><div id="ov-heat"></div></section>
      ${b.data.errors.length ? html`<section class="card"><div class="card-head"><h3>Failure reasons</h3></div><div id="ov-errors"></div></section>` : ''}`);
    const labels = o.daily.map((d) => d.day.slice(5));
    lines($('#ov-daily'), labels, [{ name: 'Analyses', values: o.daily.map((d) => d.analyses) }, { name: 'Completed', values: o.daily.map((d) => d.completed) }, { name: 'Failed', values: o.daily.map((d) => d.failed) }]);
    lines($('#ov-visitors'), labels, [{ name: 'Visitors', values: o.daily.map((d) => d.visitors) }, { name: 'Page views', values: o.daily.map((d) => d.pageViews) }]);
    const bars = (sel, rows, opts = {}) => rankedBars($(sel), rows.map((r) => ({ name: r.label || r.key || 'Unknown', count: r.count, label: opts.label?.(r) })), opts);
    bars('#ov-verdicts', b.data.verdicts.map((r) => ({ ...r, label: verdictName(r.key) })));
    bars('#ov-sentiment', b.data.commentSentiment);
    bars('#ov-langs', b.data.commentLanguages, { label: (r) => `avg score ${fmt.signed(r.avg * 100)}` });
    bars('#ov-countries', b.data.countries);
    bars('#ov-profiles', b.data.profiles, { label: (r) => `avg ${fmt.ms(r.avg)}` });
    bars('#ov-devices', b.data.devices);
    heatmap($('#ov-heat'), b.data.heatmap);
    if (b.data.errors.length) bars('#ov-errors', b.data.errors, { color: 'var(--neg)' });
  } catch (e) {
    setHTML(box, html`<div class="banner warn">${e.message}</div>`);
  }
}

function filterBar(fields) {
  return html`<div class="log-filters">${fields.map((f) => f.type === 'select'
    ? html`<select data-f="${f.key}" aria-label="${f.label}"><option value="">${f.label}: any</option>${f.options.map((o) => html`<option value="${o[0]}">${o[1]}</option>`)}</select>`
    : html`<input type="search" data-f="${f.key}" placeholder="${f.label}" aria-label="${f.label}">`)}</div>`;
}
function wireFilters(box, onChange) {
  $$('[data-f]', box).forEach((el) => {
    el.value = state.filters[el.dataset.f] || '';
    el.addEventListener(el.tagName === 'SELECT' ? 'change' : 'input', debounce(() => { state.filters[el.dataset.f] = el.value; state.page = 1; onChange(); }, 300));
  });
}
function filterQuery() {
  const p = new URLSearchParams(rangeQuery());
  for (const [k, v] of Object.entries(state.filters)) if (v) p.set(k, v);
  p.set('page', String(state.page));
  p.set('size', '50');
  return p.toString();
}
function pager(total, onPage) {
  const pages = Math.max(1, Math.ceil(total / 50));
  const box = document.createElement('div');
  box.className = 'pager';
  setHTML(box, html`<span>${fmt.full(total)} rows · page ${state.page} of ${pages}</span><div class="btns"><button type="button" class="small" ${state.page <= 1 ? 'disabled' : ''} data-p="-1">Previous</button><button type="button" class="small" ${state.page >= pages ? 'disabled' : ''} data-p="1">Next</button></div>`);
  box.addEventListener('click', (e) => { const b = e.target.closest('button[data-p]'); if (!b || b.disabled) return; state.page += Number(b.dataset.p); onPage(); });
  return box;
}

const VERDICT_OPTIONS = [['loved', 'Loved'], ['liked', 'Liked'], ['leaning-positive', 'Leaning positive'], ['mixed', 'Mixed'], ['divided', 'Divided'], ['leaning-negative', 'Leaning negative'], ['disliked', 'Disliked'], ['rejected', 'Rejected'], ['thin', 'Too few']];

async function renderAnalyses(onlyFeedback = false) {
  const box = $(onlyFeedback ? '#tab-feedback' : '#tab-analyses');
  if (!box.dataset.ready) {
    setHTML(box, html`${filterBar(onlyFeedback
      ? [{ key: 'q', label: 'Search title, channel, note, IP…' }, { key: 'liked', type: 'select', label: 'Answer', options: [['liked', 'Liked'], ['disliked', 'Disliked']] }]
      : [{ key: 'q', label: 'Search title, channel, video id, IP, user…' }, { key: 'status', type: 'select', label: 'Status', options: [['completed', 'Completed'], ['failed', 'Failed'], ['canceled', 'Canceled'], ['queued', 'Queued'], ['analysing', 'Running']] }, { key: 'verdict', type: 'select', label: 'Verdict', options: VERDICT_OPTIONS }, { key: 'profile', type: 'select', label: 'Profile', options: [['fast', 'Quick'], ['balanced', 'Standard'], ['thorough', 'Deep']] }, { key: 'country', label: 'Country' }])}
      <div class="table-wrap"><table class="data"><thead><tr><th>When</th><th>Video</th><th>Profile</th><th>Status</th><th class="num">Comments</th><th class="num">Score</th><th>Verdict</th><th>Lang</th><th>Who</th><th>Feedback</th><th class="num">Views</th></tr></thead><tbody class="an-body"></tbody></table></div><div class="an-pager"></div>
      <p class="note" style="margin-top:8px">Export one day as CSV: <span class="an-export"></span></p>`);
    box.dataset.ready = '1';
    wireFilters(box, () => renderAnalyses(onlyFeedback));
  }
  const q = new URLSearchParams(filterQuery());
  if (onlyFeedback && !q.get('liked')) q.set('liked', 'any');
  try {
    const r = await api.admin.analyses(q.toString());
    setHTML($('.an-body', box), r.rows.length ? html`${r.rows.map((x) => html`<tr class="row-link" data-id="${x.analysis_id}">
      <td title="${x.created_utc}">${fmt.dateTime(x.created_utc)}</td>
      <td class="clip" title="${x.title || x.video_id}">${x.title || x.video_id || ''}${x.channel ? html`<br><span class="faint">${x.channel}</span>` : ''}</td>
      <td>${x.profile || ''}${x.cached ? ' (cache)' : ''}</td>
      <td><span class="state ${x.status === 'completed' ? 'ok' : x.status === 'failed' ? 'err' : 'warn'}">${x.status}</span>${x.error ? html`<br><span class="faint">${x.error}</span>` : ''}</td>
      <td class="num">${x.analysed ?? ''}${x.sampled ? '*' : ''}</td><td class="num">${x.score == null ? '' : fmt.signed(x.score)}</td><td>${x.verdict ? verdictName(x.verdict) : ''}</td><td>${x.primary_language || ''}${x.language_count > 1 ? ` +${x.language_count - 1}` : ''}</td>
      <td>${x.user_name || x.ip || ''}${x.ip_country ? html`<br><span class="faint">${x.ip_city ? x.ip_city + ', ' : ''}${x.ip_country}</span>` : ''}</td>
      <td>${x.liked == null ? '' : x.liked ? '👍' : '👎'}${x.feedback_note ? html`<br><span class="faint">${x.feedback_note}</span>` : ''}</td><td class="num">${x.report_views ?? 0}</td></tr>`)}` : html`<tr><td colspan="11" class="muted">No rows in this range.</td></tr>`);
    $('.an-pager', box).replaceChildren(pager(r.total, () => renderAnalyses(onlyFeedback)));
    setHTML($('.an-export', box), html`<a href="/api/admin/export/analyses/${state.to}.csv">analyses ${state.to}</a> · <a href="/api/admin/export/comments/${state.to}.csv">comments ${state.to}</a> · <a href="/api/admin/export/requests/${state.to}.csv">requests ${state.to}</a>`);
    $('.an-body', box).onclick = (e) => { const tr = e.target.closest('tr[data-id]'); if (tr && !e.target.closest('a')) openDetail(tr.dataset.id); };
  } catch (e) { toast(e.message); }
}
function renderFeedback() { return renderAnalyses(true); }

async function openDetail(id) {
  const overlay = document.createElement('div');
  overlay.className = 'detail';
  setHTML(overlay, html`<div class="panel" role="dialog" aria-modal="true"><div class="panel-head"><h3>Analysis ${id}</h3><button type="button" class="icon-btn small" aria-label="Close"><span data-icon="x"></span></button></div><p class="muted">Loading…</p></div>`);
  document.body.append(overlay);
  const close = () => overlay.remove();
  overlay.addEventListener('click', (e) => { if (e.target === overlay || e.target.closest('button[aria-label=Close]')) close(); });
  document.addEventListener('keydown', function esc(e) { if (e.key === 'Escape') { close(); document.removeEventListener('keydown', esc); } });
  try {
    const d = await api.admin.analysis(id, 'size=100');
    const row = d.row;
    const panel = $('.panel', overlay);
    setHTML(panel, html`<div class="panel-head"><h3>${row.title || row.video_id || id}</h3><div><a class="ghost small" href="/r/${id}" target="_blank" rel="noopener">Open report</a> <button type="button" class="icon-btn small" aria-label="Close"><span data-icon="x"></span></button></div></div>
      <div class="kv">${Object.entries(row).filter(([k, v]) => k !== 'id' && v != null && v !== '').map(([k, v]) => html`<div><span>${k}</span><span>${String(v).length > 300 ? String(v).slice(0, 300) + '…' : String(v)}</span></div>`)}</div>
      <h3 style="margin:16px 0 8px">Scored comments (${fmt.full(d.comments.total)})</h3>
      <div class="table-wrap" style="max-height:360px"><table class="data"><thead><tr><th>Text</th><th>Lang</th><th>Sentiment</th><th class="num">Score</th><th class="num">Likes</th><th>Author</th></tr></thead><tbody>${d.comments.rows.map((c) => html`<tr><td class="clip" title="${c.text}">${c.text}</td><td>${c.lang}</td><td>${c.sentiment}</td><td class="num">${fmt.signed((c.score || 0) * 100)}</td><td class="num">${c.like_count}</td><td>${c.author || ''}</td></tr>`)}</tbody></table></div>
      <h3 style="margin:16px 0 8px">Requests about this analysis (${d.requests.length})</h3>
      <div class="table-wrap" style="max-height:240px"><table class="data"><thead><tr><th>When</th><th>Kind</th><th>Path</th><th class="num">Status</th><th class="num">ms</th><th>Who</th></tr></thead><tbody>${d.requests.map((q) => html`<tr><td>${fmt.dateTime(q.ts_utc)}</td><td>${q.kind}</td><td class="clip">${q.method} ${q.path}</td><td class="num">${q.status}</td><td class="num">${q.duration_ms}</td><td>${q.ip || ''} ${q.browser || ''}</td></tr>`)}</tbody></table></div>`);
  } catch (e) { setHTML($('.panel', overlay), html`<div class="panel-head"><h3>${id}</h3><button type="button" class="icon-btn small" aria-label="Close"><span data-icon="x"></span></button></div><p class="err-text">${e.message}</p>`); }
}

async function renderRequests() {
  const box = $('#tab-requests');
  if (!box.dataset.ready) {
    setHTML(box, html`${filterBar([{ key: 'q', label: 'Search path, IP, user agent…' }, { key: 'kind', type: 'select', label: 'Kind', options: [['page', 'Page'], ['report-page', 'Report page'], ['analyse', 'Analyse'], ['job-status', 'Job status'], ['events', 'Events'], ['report', 'Report API'], ['feedback', 'Feedback'], ['export', 'Export'], ['recent', 'Recent'], ['system', 'System'], ['admin', 'Admin'], ['auth', 'Auth'], ['static', 'Static'], ['other', 'Other']] }, { key: 'status', type: 'select', label: 'Status', options: [['2xx', '2xx'], ['3xx', '3xx'], ['4xx', '4xx'], ['5xx', '5xx']] }, { key: 'ip', label: 'IP' }])}
      <div class="table-wrap"><table class="data"><thead><tr><th>When</th><th>Kind</th><th>Request</th><th class="num">Status</th><th class="num">ms</th><th>Who</th><th>Client</th><th>Detail</th></tr></thead><tbody id="rq-body"></tbody></table></div><div id="rq-pager"></div>`);
    box.dataset.ready = '1';
    wireFilters(box, renderRequests);
  }
  try {
    const r = await api.admin.requests(filterQuery());
    setHTML($('#rq-body', box), r.rows.length ? html`${r.rows.map((x) => html`<tr><td>${fmt.dateTime(x.ts_utc)}</td><td>${x.kind}</td><td class="clip" title="${x.path}${x.query || ''}">${x.method} ${x.path}${x.query ? html`<span class="faint">${x.query}</span>` : ''}</td><td class="num">${x.status}</td><td class="num">${x.duration_ms}</td><td>${x.user_name || x.ip || ''}${x.ip_country ? html`<br><span class="faint">${x.ip_country}</span>` : ''}</td><td>${[x.browser, x.os, x.device].filter(Boolean).join(' · ')}</td><td class="clip">${x.detail || ''}${x.analysis_id ? html` <a href="/r/${x.analysis_id}" target="_blank" rel="noopener">${x.analysis_id}</a>` : ''}</td></tr>`)}` : html`<tr><td colspan="8" class="muted">No rows in this range.</td></tr>`);
    $('#rq-pager', box).replaceChildren(pager(r.total, renderRequests));
  } catch (e) { toast(e.message); }
}

async function renderStore() {
  const box = $('#tab-store');
  setHTML(box, html`<p class="muted">Loading…</p>`);
  try {
    const s = await api.admin.store();
    setHTML(box, html`<section class="card"><div class="card-head"><h3>Audit database files</h3><p class="sub">one SQLite file per month in <code>${s.directory}</code>; inside, a table set per day (requests_, analyses_, comments_)</p><div class="tools"><button type="button" class="small" id="st-refresh"><span data-icon="refresh"></span> Refresh counts</button></div></div>
      ${s.months.length ? s.months.map((m) => html`<div class="month"><div class="month-head"><b>${m.month}</b><span class="muted">${fmt.bytes(m.bytes)} · schema v${m.schema || '?'} · ${m.path}</span></div>
        ${m.error ? html`<p class="err-text">${m.error}</p>` : html`<div class="table-wrap" style="margin-top:8px"><table class="data"><thead><tr><th>Day</th><th class="num">Requests</th><th class="num">Analyses</th><th class="num">Completed</th><th class="num">Comments</th><th class="num">Feedback</th><th class="num">Liked</th><th>Counted</th><th>Export</th></tr></thead>
          <tbody>${m.days.map((d) => html`<tr><td>${d.day}</td><td class="num">${fmt.full(d.requests)}</td><td class="num">${fmt.full(d.analyses)}</td><td class="num">${fmt.full(d.completed)}</td><td class="num">${fmt.full(d.comments)}</td><td class="num">${fmt.full(d.feedback)}</td><td class="num">${fmt.full(d.liked)}</td><td class="faint">${d.updatedUtc ? fmt.dateTime(d.updatedUtc) : 'pending'}</td><td><a href="/api/admin/export/analyses/${d.day}.csv">analyses</a> · <a href="/api/admin/export/comments/${d.day}.csv">comments</a> · <a href="/api/admin/export/requests/${d.day}.csv">requests</a></td></tr>`)}</tbody></table></div>`}
      </div>`) : html`<p class="muted">No audit files yet.</p>`}</section>`);
    $('#st-refresh').addEventListener('click', async () => { try { await api.admin.refresh(); toast('Counts refreshed.'); renderStore(); } catch (e) { toast(e.message); } });
  } catch (e) { setHTML(box, html`<div class="banner warn">${e.message}</div>`); }
}

async function renderSystem() {
  const box = $('#tab-system');
  setHTML(box, html`<p class="muted">Loading…</p>`);
  try {
    const s = await api.admin.status();
    const w = s.audit.writer;
    setHTML(box, html`
      <div class="grid two">
        <section class="card"><div class="card-head"><h3>Models</h3><div class="tools"><button type="button" class="small" id="sy-unload">Free memory</button></div></div>
          <div class="status-list">${s.models.map((m) => html`<div class="item"><div><b>${m.displayName}</b><span class="muted">${m.role} · ${m.sizeMb} MB · ${m.license}</span><br><span class="faint mono">${m.repo} / ${m.file}</span>${m.state.error ? html`<br><span class="err-text">${m.state.error}</span>` : ''}${m.loaded ? html`<br><span class="faint">loaded ${fmt.ago(m.loaded.loadedAt)}, last used ${fmt.ago(m.loaded.lastUsedAt)}</span>` : ''}</div>
            <div><span class="state ${m.state.state}">${m.enabled ? m.state.state + (m.state.state === 'downloading' && m.state.fraction != null ? ` ${Math.round(m.state.fraction * 100)}%` : '') : 'disabled'}</span>${m.enabled && m.state.state !== 'ready' && m.state.state !== 'downloading' ? html`<br><button type="button" class="ghost small" data-dl="${m.role}">Download now</button>` : ''}</div></div>`)}</div>
          <p class="note">${fmt.bytes(s.modelBytes)} on disk in <code>${s.paths.models}</code>. Language detection: ${s.analysis.languageDetection}.</p>
        </section>
        <section class="card"><div class="card-head"><h3>Analysis engine</h3></div>
          <div class="kv">
            <div><span>Default profile</span><span>${s.analysis.defaultProfile}</span></div><div><span>Allowed profiles</span><span>${s.analysis.allowedProfiles.join(', ')}</span></div>
            <div><span>Inference threads</span><span>${s.analysis.threads} of ${s.server.cores} cores</span></div><div><span>Concurrent analyses</span><span>${s.analysis.maxConcurrentAnalyses}</span></div>
            <div><span>Time budget</span><span>${s.analysis.timeBudgetSeconds} s</span></div><div><span>Max comments</span><span>${s.analysis.maxComments} (+${s.analysis.repliesPerThread} replies/thread)</span></div>
            <div><span>Batch / tokens</span><span>${s.analysis.batchSize} × ${s.analysis.maxSequenceTokens}</span></div><div><span>Unload after idle</span><span>${s.analysis.unloadIdleMinutes} min</span></div>
            <div><span>Report cache</span><span>${s.analysis.cacheHours} h</span></div><div><span>Report retention</span><span>${s.analysis.reportRetentionDays} days · ${fmt.full(s.analysis.storedReports)} stored</span></div>
            <div><span>Queue</span><span>${s.analysis.queued} queued · ${s.analysis.running} running</span></div><div><span>Working set</span><span>${s.server.workingSetMb} MB</span></div>
          </div>
          ${s.analysis.active.length ? html`<h4 style="margin:12px 0 6px">Active now</h4><div class="status-list">${s.analysis.active.map((j) => html`<div class="item"><div><b>${j.videoId} · ${j.profile}</b><span class="muted">${j.message}</span></div><span class="state warn">${j.state}</span></div>`)}</div>` : ''}
        </section>
        <section class="card"><div class="card-head"><h3>YouTube access</h3><div class="tools">${s.tools.ytDlp.owned ? html`<button type="button" class="small" id="sy-update">Update yt-dlp</button>` : ''}</div></div>
          <div class="kv">
            <div><span>Comment source</span><span>${s.youtube.source} <span class="state ${s.youtube.available ? 'ok' : 'err'}">${s.youtube.available ? 'available' : 'unavailable'}</span></span></div>
            <div><span>Fallback</span><span>${s.youtube.fallback || 'none'}</span></div>
            <div><span>Data API key</span><span>${s.youtube.apiKeyConfigured ? 'configured' : 'not configured'}</span></div>
            <div><span>yt-dlp</span><span>${s.tools.ytDlp.ok ? s.tools.ytDlp.version : 'missing'} ${s.tools.ytDlp.owned ? '(managed by the app)' : ''}</span></div>
            <div><span>yt-dlp path</span><span class="mono">${s.tools.ytDlp.path || ''}</span></div>
            <div><span>JavaScript runtime</span><span>${s.tools.jsRuntime.ok ? `${s.tools.jsRuntime.name} ${s.tools.jsRuntime.version}` : 'none (optional)'}</span></div>
          </div>
          ${s.youtube.problem ? html`<p class="err-text">${s.youtube.problem}</p>` : ''}${s.tools.ytDlp.note ? html`<p class="note">${s.tools.ytDlp.note}</p>` : ''}
        </section>
        <section class="card"><div class="card-head"><h3>Audit log</h3></div>
          <div class="kv">
            <div><span>Writer</span><span><span class="state ${w.state}">${w.state}</span></span></div><div><span>Rows written</span><span>${fmt.full(w.written)}</span></div>
            <div><span>Queued / dropped / failed</span><span>${w.queued} / ${w.dropped} / ${w.failed}</span></div><div><span>Last write</span><span>${w.lastWriteAt ? fmt.ago(w.lastWriteAt) : '–'}</span></div>
            <div><span>Open months</span><span>${w.openMonths.join(', ') || '–'}</span></div><div><span>Files</span><span>${s.audit.months.length} (${fmt.bytes(s.audit.months.reduce((a, m) => a + m.bytes, 0))})</span></div>
            <div><span>Retention</span><span>${s.audit.retentionMonths} months</span></div><div><span>IP mode</span><span>${s.audit.ipMode}</span></div>
            <div><span>Time zone</span><span>${s.audit.timeZone}</span></div><div><span>Comments stored</span><span>${s.audit.storeComments ? 'yes' : 'no'}</span></div>
            <div><span>Request log</span><span>${s.audit.logRequests ? 'on' : 'off'} (excluding ${s.audit.excludePaths.join(', ')})</span></div><div><span>Maintenance</span><span>${s.audit.maintenance.lastRunAt ? fmt.ago(s.audit.maintenance.lastRunAt) : 'not yet'}</span></div>
          </div>
          ${w.lastError ? html`<p class="err-text">${w.lastError} (${fmt.ago(w.lastErrorAt)})</p>` : ''}${s.audit.timeZoneNote ? html`<p class="note">${s.audit.timeZoneNote}</p>` : ''}
          <h4 style="margin:12px 0 6px">GeoIP</h4>
          <div class="kv"><div><span>State</span><span><span class="state ${s.geoip.state === 'ready' ? 'ok' : s.geoip.state === 'off' ? '' : 'warn'}">${s.geoip.state}</span></span></div><div><span>Edition</span><span>${s.geoip.edition || '–'} ${s.geoip.month || ''}</span></div></div>
          ${s.geoip.error ? html`<p class="err-text">${s.geoip.error}</p>` : ''}<p class="note">${s.geoip.attribution}</p>
        </section>
      </div>
      <section class="card"><div class="card-head"><h3>Server</h3></div><div class="kv">
        <div><span>App version</span><span>${s.server.version}</span></div><div><span>Machine</span><span>${s.server.machine}</span></div><div><span>OS</span><span>${s.server.os} (${s.server.arch}${s.server.is64Bit ? ', 64-bit' : ', 32-bit!'})</span></div><div><span>.NET</span><span>${s.server.dotnet}</span></div>
        <div><span>Data root</span><span class="mono">${s.paths.dataRoot}</span></div><div><span>Server time</span><span>${fmt.dateTime(s.server.time)}</span></div></div></section>`);
    $('#sy-unload').addEventListener('click', async () => { await api.admin.unload(); toast('Models released.'); renderSystem(); });
    $('#sy-update')?.addEventListener('click', async () => { try { const r = await api.admin.updateTool(); toast(`yt-dlp ${r.version || 'updated'}`); renderSystem(); } catch (e) { toast(e.message); } });
    $$('button[data-dl]', box).forEach((b) => b.addEventListener('click', async () => { await api.admin.download(b.dataset.dl); toast('Download started.'); setTimeout(renderSystem, 1500); }));
  } catch (e) { setHTML(box, html`<div class="banner warn">${e.message}</div>`); }
}
