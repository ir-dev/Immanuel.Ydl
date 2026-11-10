// The page: paste a link → watch the analysis → read the report. Hash/path routing: "/" home, "/r/{id}" report.
import { $, $$, html, setHTML, esc, fmt, verdictName, scoreClass, toast, initTheme, store, safeUrl } from './util.js';
import { api, watchJob } from './api.js';
import { sentimentTimeline, shareBars, rankedBars, tableTwin, tableToggle } from './charts.js';

const state = { system: null, profiles: null, profile: store.get('ark-profile', null), job: null, report: null, abort: null };

initTheme();
boot();

async function boot() {
  wireForm();
  const reportId = document.body.dataset.report || (location.pathname.match(/^\/r\/([A-Za-z0-9-]{17,24})$/) || [])[1];
  loadSystem();
  loadProfiles();
  if (reportId) openReport(reportId, { replace: true });
  else loadRecent();
  window.addEventListener('popstate', () => {
    const m = location.pathname.match(/^\/r\/([A-Za-z0-9-]{17,24})$/);
    if (m) openReport(m[1], { replace: true, silent: true });
    else showHome();
  });
  // share-target / ?url= deep link
  const u = new URLSearchParams(location.search).get('url') || new URLSearchParams(location.search).get('text');
  if (u && /youtu/i.test(u)) { $('#url').value = u.match(/https?:\/\/\S+/)?.[0] || u; start(); }
}

// ── system status ────────────────────────────────────────────────────────
async function loadSystem() {
  try {
    const s = await api.system();
    state.system = s;
    const pill = $('#sys-pill');
    const banner = $('#sys-banner');
    $('#foot-version').textContent = `${s.app} comment sentiment ${s.version} · sentiment ${s.models.find((m) => m.role === 'Sentiment')?.slug || ''} · language detection: ${s.languageDetection}`;
    if (s.signedIn) { $('#user-box').hidden = false; $('#user-name').textContent = s.user || ''; }
    if (s.showRecent) $('#nav-recent').hidden = false;
    if (s.ready) {
      pill.hidden = true;
      banner.hidden = true;
    } else if (s.preparing) {
      pill.hidden = false;
      pill.className = 'pill warn';
      const d = s.downloading[0];
      setHTML(pill, html`<span class="dot pulse"></span>Preparing models${d?.fraction != null ? ` ${Math.round(d.fraction * 100)}%` : ''}`);
      banner.hidden = false;
      banner.className = 'banner info';
      setHTML(banner, html`<b>First start: the language models are downloading.</b> ${s.downloading.length ? `${s.downloading.map((x) => x.role).join(', ')} — ${Math.round((d?.fraction || 0) * 100)}%` : 'Waiting for the download to begin'}. You can queue an analysis now; it starts as soon as the models are on disk.`);
      setTimeout(loadSystem, 4000);
    } else {
      pill.hidden = false;
      pill.className = 'pill err';
      setHTML(pill, html`<span class="dot"></span>Not ready`);
      banner.hidden = false;
      banner.className = 'banner warn';
      const missing = s.models.filter((m) => m.enabled && m.state !== 'ready' && m.error).map((m) => `${m.displayName}: ${m.error}`);
      setHTML(banner, html`<b>This server isn't ready yet.</b> ${!s.source.available ? s.source.problem : ''} ${missing.join(' · ')}`);
      setTimeout(loadSystem, 15000);
    }
  } catch (e) {
    const pill = $('#sys-pill');
    pill.hidden = false;
    pill.className = 'pill err';
    pill.textContent = 'Offline';
    setTimeout(loadSystem, 10000);
  }
}

async function loadProfiles() {
  try {
    const p = await api.profiles();
    state.profiles = p;
    if (!state.profile || !p.profiles.some((x) => x.id === state.profile)) state.profile = p.default;
    const row = $('#profile-row');
    setHTML(row, html`${p.profiles.map((x) => html`
      <label title="${x.blurb}">
        <input type="radio" name="profile" value="${x.id}" ${x.id === state.profile ? 'checked' : ''}>
        ${x.name} <span class="hint">· up to ${fmt.count(x.maxComments)} comments</span>
      </label>`)}`);
    row.addEventListener('change', (e) => { if (e.target.name === 'profile') { state.profile = e.target.value; store.set('ark-profile', state.profile); } });
  } catch { /* the server will use its default */ }
}

// ── form ─────────────────────────────────────────────────────────────────
function wireForm() {
  const input = $('#url');
  const clear = $('#btn-clear');
  input.addEventListener('input', () => { clear.hidden = !input.value; $('#error-banner').hidden = true; });
  clear.addEventListener('click', () => { input.value = ''; clear.hidden = true; input.focus(); });
  $('#btn-paste').addEventListener('click', async () => {
    try { const t = await navigator.clipboard.readText(); if (t) { input.value = t.trim(); clear.hidden = false; start(); } }
    catch { toast('Paste with Ctrl+V / ⌘V — the browser blocked clipboard access.'); input.focus(); }
  });
  $('#url-form').addEventListener('submit', (e) => { e.preventDefault(); start(); });
}

async function start(fresh = false) {
  const url = $('#url').value.trim();
  const err = $('#error-banner');
  err.hidden = true;
  if (!url) { showError('Paste a YouTube link first.'); $('#url').focus(); return; }
  const go = $('#btn-go');
  go.disabled = true;
  try {
    const job = await api.analyse(url, state.profile, fresh);
    if (job.cached && job.reportId) {
      toast('Analysed recently — showing that report. Use “Re-run” for a fresh one.');
      await openReport(job.reportId);
      return;
    }
    await followJob(job);
  } catch (e) {
    showError(e.message, e.status === 429 ? 'The server limits how many analyses run at once. Try again in a minute.' : e.detail);
  } finally {
    go.disabled = false;
  }
}

function showError(msg, detail) {
  const err = $('#error-banner');
  err.hidden = false;
  setHTML(err, html`<b>${msg}</b>${detail ? html` <span class="muted">${detail}</span>` : ''}`);
}

// ── progress ─────────────────────────────────────────────────────────────
const STEPS = [['queued', 'Queued'], ['fetching', 'Fetching comments'], ['languages', 'Languages'], ['sentiment', 'Sentiment'], ['themes', 'Themes'], ['toxicity', 'Toxicity'], ['emotion', 'Emotions'], ['report', 'Report']];

async function followJob(job) {
  state.job = job;
  state.abort?.abort();
  state.abort = new AbortController();
  $('#report').replaceChildren();
  $('#recent').hidden = true;
  $('#hero').classList.add('compact');
  const box = $('#progress');
  box.hidden = false;
  renderProgress(job);
  box.scrollIntoView({ behavior: 'smooth', block: 'start' });
  try {
    const final = await watchJob(job.id, renderProgress, { signal: state.abort.signal });
    if (final.state === 'completed' && final.reportId) {
      box.hidden = true;
      await openReport(final.reportId);
    } else if (final.state === 'failed') {
      renderProgress(final);
    }
  } catch (e) {
    if (e.name !== 'AbortError') { showError(e.message); box.hidden = true; }
  }
}

function renderProgress(s) {
  const box = $('#progress');
  const stageIdx = STEPS.findIndex(([k]) => k === s.stage || (k === 'fetching' && s.state === 'fetching') || (k === 'queued' && s.state === 'queued'));
  const failed = s.state === 'failed';
  const canceled = s.state === 'canceled';
  setHTML(box, html`
    <div class="progress-head">
      <img src="https://i.ytimg.com/vi/${s.videoId}/mqdefault.jpg" alt="" loading="lazy">
      <div>
        <div class="progress-title">${failed ? 'Analysis failed' : canceled ? 'Canceled' : s.state === 'queued' ? 'Waiting in the queue' : 'Analysing comments'} <span class="muted">· ${s.profileName}</span></div>
        <div class="progress-msg">${failed ? s.error : s.message}${s.queuePosition > 1 ? ` · ${s.queuePosition - 1} ahead of you` : ''}</div>
        ${failed && s.errorDetail ? html`<div class="note">${s.errorDetail}</div>` : ''}
      </div>
    </div>
    ${failed || canceled ? '' : html`<div class="bar ${s.progress == null || s.progress === 0 ? 'indeterminate' : ''}" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${Math.round((s.progress || 0) * 100)}"><i style="width:${Math.round((s.progress || 0) * 100)}%"></i></div>`}
    <ul class="steps">${STEPS.filter(([k]) => state.profiles ? stepApplies(k) : true).map(([k, name], i) => html`<li class="${i < stageIdx ? 'done' : i === stageIdx && !failed ? 'now' : ''}">${i < stageIdx ? '✓ ' : ''}${name}</li>`)}</ul>
    <div class="actions">
      ${failed ? html`<button type="button" class="ghost small" id="btn-retry">${s.retryable ? 'Try again' : 'Back'}</button>` : ''}
      ${!failed && !canceled ? html`<button type="button" class="ghost small" id="btn-cancel">Cancel</button>` : ''}
    </div>`);
  $('#btn-cancel')?.addEventListener('click', async () => { try { await api.cancel(s.id); } catch { /* already done */ } });
  $('#btn-retry')?.addEventListener('click', () => { box.hidden = true; if (s.retryable) start(true); else showHome(); });
}
function stepApplies(k) {
  const p = state.profiles?.profiles.find((x) => x.id === state.profile);
  if (!p) return true;
  if (k === 'themes') return p.themes;
  if (k === 'toxicity') return p.toxicity;
  if (k === 'emotion') return p.emotion;
  return true;
}

// ── report ───────────────────────────────────────────────────────────────
async function openReport(id, { replace = false, silent = false } = {}) {
  const box = $('#report');
  $('#hero').classList.add('compact');
  $('#recent').hidden = true;
  $('#progress').hidden = true;
  setHTML(box, html`<div class="card"><div class="skeleton" style="height:28px;width:60%"></div><div class="skeleton" style="height:18px;width:40%;margin-top:10px"></div><div class="skeleton" style="height:120px;margin-top:16px"></div></div>`);
  if (!silent) history[replace ? 'replaceState' : 'pushState']({ id }, '', `/r/${id}`);
  try {
    const r = await api.report(id);
    state.report = r;
    document.title = `${r.video.title || 'Report'} · ${verdictName(r.sentiment.verdict)} · ${state.system?.app || 'ARK-RAPID'}`;
    renderReport(r);
    box.scrollIntoView({ behavior: 'smooth', block: 'start' });
  } catch (e) {
    setHTML(box, html`<div class="card"><h2>Report not found</h2><p class="muted">${e.message}</p><p><a href="/">Analyse a video</a></p></div>`);
  }
}

function showHome() {
  state.abort?.abort();
  $('#hero').classList.remove('compact');
  $('#report').replaceChildren();
  $('#progress').hidden = true;
  document.title = `${state.system?.app || 'ARK-RAPID'} · Comment sentiment`;
  loadRecent();
}

function byId(r) { const m = new Map(); for (const c of r.comments) m.set(c.id, c); return m; }

function renderReport(r) {
  const s = r.sentiment;
  const v = r.video;
  const ids = byId(r);
  const box = $('#report');
  const unanalysed = (v.commentCount || 0) > r.fetch.fetched ? v.commentCount - r.fetch.fetched : 0;
  setHTML(box, html`
  <div class="report">
    <section class="card video-head">
      <a href="${safeUrl(v.url)}" target="_blank" rel="noopener"><img src="${v.thumbnail || `https://i.ytimg.com/vi/${v.id}/hqdefault.jpg`}" alt=""></a>
      <div>
        <h2><a href="${safeUrl(v.url)}" target="_blank" rel="noopener">${v.title || v.id}</a></h2>
        <div class="video-meta">
          ${v.channel ? html`<span><b>${v.channel}</b>${v.channelFollowers ? ` · ${fmt.count(v.channelFollowers)} subscribers` : ''}</span>` : ''}
          ${v.viewCount != null ? html`<span>${fmt.count(v.viewCount)} views</span>` : ''}
          ${v.likeCount != null ? html`<span>${fmt.count(v.likeCount)} likes</span>` : ''}
          ${v.commentCount != null ? html`<span>${fmt.count(v.commentCount)} comments on YouTube</span>` : ''}
          ${v.uploadedAt ? html`<span>${fmt.date(v.uploadedAt)}</span>` : ''}
          ${v.durationSeconds ? html`<span>${fmt.duration(v.durationSeconds)}</span>` : ''}
          ${v.category ? html`<span>${v.category}</span>` : ''}
        </div>
        <div class="report-actions">
          <button type="button" class="small" id="btn-share"><span data-icon="share"></span> Share link</button>
          <a class="small" role="button" href="/api/reports/${r.id}/comments.csv" download><span data-icon="download"></span> Comments CSV</a>
          <button type="button" class="small" id="btn-print">Print / PDF</button>
          <button type="button" class="ghost small" id="btn-rerun"><span data-icon="refresh"></span> Re-run</button>
          <button type="button" class="ghost small" id="btn-new">New analysis</button>
        </div>
        <p class="note">${r.profileName} profile · ${fmt.count(r.fetch.analysed)} comments analysed${r.fetch.exactRepeats ? ` (${fmt.count(r.fetch.exactRepeats)} exact repeats)` : ''}${r.fetch.replies ? ` · ${fmt.count(r.fetch.replies)} replies` : ''}${unanalysed ? ` · ${fmt.count(unanalysed)} more on YouTube not read` : ''} · ${fmt.ago(r.createdAt)} · ${fmt.ms(r.compute.totalMs)} on ${r.compute.threads} CPU thread${r.compute.threads === 1 ? '' : 's'}</p>
        ${r.fetch.warnings?.length ? html`<p class="note"><span data-icon="warn"></span> ${r.fetch.warnings.join(' · ')}</p>` : ''}
      </div>
    </section>

    <section class="card verdict-card">
      <div>
        <div class="hero-num ${scoreClass(s.score)}" style="color:var(--${scoreClass(s.score)})">${fmt.signed(s.score)}<small>net score</small></div>
        <div class="note">positive minus negative share, −100…+100</div>
      </div>
      <div>
        <span class="verdict ${s.verdict}">${verdictName(s.verdict)}</span>
        <div class="verdict-text">${s.verdictText}</div>
        <div class="seg" role="img" aria-label="${s.positivePct}% positive, ${s.neutralPct}% neutral, ${s.negativePct}% negative">
          ${s.positivePct > 0 ? html`<span class="pos" style="flex:${s.positivePct}">${s.positivePct >= 8 ? `${Math.round(s.positivePct)}%` : ''}</span>` : ''}
          ${s.neutralPct > 0 ? html`<span class="neu" style="flex:${s.neutralPct}">${s.neutralPct >= 8 ? `${Math.round(s.neutralPct)}%` : ''}</span>` : ''}
          ${s.negativePct > 0 ? html`<span class="neg" style="flex:${s.negativePct}">${s.negativePct >= 8 ? `${Math.round(s.negativePct)}%` : ''}</span>` : ''}
        </div>
        <div class="legend"><span class="pos"><i></i>Positive ${fmt.count(s.positive)}</span><span class="neu"><i></i>Neutral ${fmt.count(s.neutral)}</span><span class="neg"><i></i>Negative ${fmt.count(s.negative)}</span></div>
      </div>
    </section>

    <section class="stats">
      ${stat('Likes-weighted score', fmt.signed(s.weightedScore), 'what the most-endorsed comments say')}
      ${stat('Polarization', fmt.pct(s.polarization * 100), s.polarization >= 0.4 ? 'two large camps' : s.polarization >= 0.2 ? 'some disagreement' : 'one-sided')}
      ${stat('Languages', String(r.languages.filter((l) => l.key !== 'und').length), r.languages[0] ? `${r.languages[0].label} ${fmt.pct(r.languages[0].pct)}` : '')}
      ${stat('Creator in the comments', String(r.threads.uploaderComments), r.threads.hearted ? `${r.threads.hearted} hearted` : 'replies by the uploader')}
      ${r.toxicity ? stat('Toxic comments', fmt.pct(r.toxicity.pct, 1), `${fmt.count(r.toxicity.flagged)} flagged`) : stat('Model confidence', fmt.pct(s.meanConfidence * 100), 'mean top-class probability')}
      ${r.engagement.commentsPerThousandViews != null ? stat('Comments per 1k views', String(r.engagement.commentsPerThousandViews), r.engagement.commentsPerDay != null ? `${r.engagement.commentsPerDay}/day since upload` : '') : stat('Average length', `${r.engagement.avgLength}`, 'characters per comment')}
    </section>

    ${r.insights.length ? html`<section class="card"><div class="card-head"><h2>What stands out</h2></div><div class="insights">${r.insights.map((i) => html`<div class="insight ${i.kind}"><b>${i.title}</b><p>${i.text}</p></div>`)}</div></section>` : ''}

    <div class="grid two">
      ${r.timeline.length ? html`<section class="card" id="sec-timeline"><div class="card-head"><h2>Mood over time</h2><p class="sub">by when the comments were posted${r.fetch.source === 'yt-dlp' ? ' (YouTube gives relative dates, so these are approximate)' : ''}</p></div><div class="chart-slot"></div></section>` : ''}
      ${r.languages.length ? html`<section class="card" id="sec-languages"><div class="card-head"><h2>Languages</h2><p class="sub">share of comments and their sentiment</p></div><div class="chart-slot"></div></section>` : ''}
    </div>

    ${r.themes.length ? html`<section class="card"><div class="card-head"><h2>What people talk about</h2><p class="sub">${r.themes.length} themes found by clustering the comments' meaning across languages</p></div>
      <div class="themes">${r.themes.map((t) => html`<div class="theme"><div class="theme-head"><b>${t.label}</b><span class="score-pill ${scoreClass(t.score)}">${fmt.signed(t.score)}</span></div>
        <div class="note">${fmt.count(t.count)} comments (${fmt.pct(t.pct)}) · ${fmt.pct((100 * t.positive) / Math.max(1, t.count))} positive · ${fmt.pct((100 * t.negative) / Math.max(1, t.count))} negative</div>
        <div class="kw">${t.keywords.map((k) => html`<span>${k}</span>`)}</div>
        ${t.quoteIds.map((id) => ids.get(id)).filter(Boolean).slice(0, 2).map((c) => html`<blockquote class="quote">${clip(c.text, 180)}<span class="by">${c.author || 'anonymous'} · ${fmt.count(c.likes)} likes · ${c.langName}</span></blockquote>`)}
      </div>`)}</div></section>` : ''}

    <div class="grid two">
      ${r.positiveKeywords.length || r.negativeKeywords.length ? html`<section class="card"><div class="card-head"><h2>Words that set the tone</h2><p class="sub">most distinctive words in positive vs negative comments</p></div>
        ${r.positiveKeywords.length ? html`<h3 class="muted">In praise</h3><div class="chips pos">${r.positiveKeywords.map((k) => html`<span>${k.key}<small>${k.count}</small></span>`)}</div>` : ''}
        ${r.negativeKeywords.length ? html`<h3 class="muted" style="margin-top:12px">In complaints</h3><div class="chips neg">${r.negativeKeywords.map((k) => html`<span>${k.key}<small>${k.count}</small></span>`)}</div>` : ''}
        ${r.emoji.length ? html`<h3 class="muted" style="margin-top:12px">Emoji</h3><div class="chips">${r.emoji.map((e) => html`<span title="net score ${fmt.signed(e.score)}">${e.key}<small>${e.count}</small></span>`)}</div>` : ''}
      </section>` : ''}
      ${r.emotions?.length ? html`<section class="card" id="sec-emotions"><div class="card-head"><h2>Emotions</h2><p class="sub">dominant emotion per comment (GoEmotions, 28 labels)</p></div><div class="chart-slot"></div></section>` : ''}
    </div>

    <section class="card" id="sec-comments">
      <div class="card-head"><h2>The voices</h2><p class="sub">tap a tab — every comment is in the table below and in the CSV</p></div>
      <div class="tabs" role="tablist" id="voice-tabs">
        ${[['mostLiked', 'Most liked'], ['mostPositive', 'Most positive'], ['mostNegative', 'Most negative'], ['creator', 'From the creator'], ['questions', 'Questions'], ['mostRepeated', 'Repeated'], ['toxic', 'Flagged toxic']].filter(([k]) => r.highlights[k]?.length).map(([k, name], i) => html`<button role="tab" data-key="${k}" aria-selected="${i === 0}">${name}</button>`)}
      </div>
      <div class="comments" id="voice-list"></div>
      <details style="margin-top:14px"><summary class="muted">All ${fmt.count(r.comments.length)} analysed comments</summary><div class="table-wrap" style="margin-top:10px;max-height:480px" id="all-comments"></div></details>
    </section>

    <section class="card feedback" id="feedback">
      <b>Was this report useful?</b>
      <div class="thumbs"><button type="button" class="small" data-liked="true" aria-label="Yes"><span data-icon="thumb-up"></span> Yes</button><button type="button" class="small" data-liked="false" aria-label="No"><span data-icon="thumb-down"></span> No</button></div>
      <textarea id="fb-note" placeholder="Anything we got wrong or you'd want to see? (optional)" maxlength="1000"></textarea>
      <button type="button" class="primary small" id="fb-send" hidden>Send</button>
      <span class="note" id="fb-thanks" hidden>Thank you — noted.</span>
    </section>

    <section class="card"><div class="card-head"><h3>How this was computed</h3></div>
      <div class="models-list">
        ${r.models.map((m) => html`<div><b>${m.role}</b> · ${m.name} · ${fmt.count(m.items)} comments in ${fmt.ms(m.ms)}</div>`)}
        <div><b>source</b> · ${r.fetch.source}${r.fetch.toolVersion ? ` ${r.fetch.toolVersion}` : ''} · fetched ${fmt.count(r.fetch.fetched)} comments in ${fmt.ms(r.compute.fetchMs)}</div>
        <div><b>language detection</b> · fastText lid.176 with a script fallback · <b>themes</b> · spherical k-means on MiniLM sentence vectors</div>
        ${r.fetch.sampled ? html`<div><b>sampling</b> · ${r.fetch.sampleNote}</div>` : ''}
        <div class="note">Scores are a model's reading of public comments. Sarcasm, in-jokes and mixed statements are where models fail most; the quotes are there so you can check.</div>
      </div>
    </section>
  </div>`);

  // charts
  const tl = $('#sec-timeline .chart-slot');
  if (tl) {
    sentimentTimeline(tl, r.timeline);
    const twin = tableTwin(tl, ['Period', 'Comments', 'Positive', 'Neutral', 'Negative', 'Score'], r.timeline.map((b) => [b.label, b.count, b.positive, b.neutral, b.negative, b.score == null ? '' : Math.round(b.score)]));
    tableToggle($('#sec-timeline .card-head'), twin);
  }
  const lg = $('#sec-languages .chart-slot');
  if (lg) {
    shareBars(lg, r.languages.map((l) => ({ name: l.label || l.key, count: l.count, positive: l.positive, neutral: l.neutral, negative: l.negative, score: l.score })), { total: r.fetch.analysed });
    const twin = tableTwin(lg, ['Language', 'Comments', 'Share %', 'Positive', 'Neutral', 'Negative', 'Score'], r.languages.map((l) => [l.label || l.key, l.count, l.pct, l.positive, l.neutral, l.negative, l.score == null ? '' : Math.round(l.score)]));
    tableToggle($('#sec-languages .card-head'), twin);
  }
  const em = $('#sec-emotions .chart-slot');
  if (em) rankedBars(em, r.emotions.filter((e) => e.key !== 'neutral').map((e) => ({ name: e.label || e.key, count: e.count, label: `comments (${fmt.pct(e.pct)})` })), { max: 10 });

  // voices
  const tabs = $('#voice-tabs');
  const renderVoices = (key) => {
    const list = $('#voice-list');
    const items = (r.highlights[key] || []).map((id) => ids.get(id)).filter(Boolean);
    setHTML(list, items.length ? html`${items.map((c) => commentCard(c, r))}` : html`<p class="muted">Nothing here.</p>`);
  };
  tabs.addEventListener('click', (e) => {
    const b = e.target.closest('button[data-key]');
    if (!b) return;
    $$('button', tabs).forEach((x) => x.setAttribute('aria-selected', String(x === b)));
    renderVoices(b.dataset.key);
  });
  renderVoices($('button[data-key]', tabs)?.dataset.key);
  renderAllComments(r);

  // actions
  $('#btn-share').addEventListener('click', async () => {
    const link = `${location.origin}/r/${r.id}`;
    try {
      if (navigator.share) await navigator.share({ title: document.title, url: link });
      else { await navigator.clipboard.writeText(link); toast('Link copied.'); }
    } catch { toast(link); }
  });
  $('#btn-print').addEventListener('click', () => print());
  $('#btn-rerun').addEventListener('click', () => { $('#url').value = r.video.url; start(true); });
  $('#btn-new').addEventListener('click', () => { history.pushState({}, '', '/'); showHome(); $('#url').value = ''; $('#url').focus(); window.scrollTo({ top: 0, behavior: 'smooth' }); });

  // feedback
  const fb = $('#feedback');
  let liked = null;
  fb.addEventListener('click', async (e) => {
    const b = e.target.closest('button[data-liked]');
    if (b) {
      liked = b.dataset.liked === 'true';
      $$('button[data-liked]', fb).forEach((x) => x.classList.toggle('is-on', x === b));
      $('#fb-send').hidden = false;
      if (!$('#fb-note').value) await sendFeedback();
      return;
    }
    if (e.target.closest('#fb-send')) await sendFeedback();
  });
  async function sendFeedback() {
    if (liked == null) return;
    try {
      await api.feedback(r.id, liked, $('#fb-note').value.trim() || null);
      $('#fb-thanks').hidden = false;
      $('#fb-send').hidden = true;
    } catch (err) { toast(err.message); }
  }
}

function stat(label, value, delta) {
  return html`<div class="stat"><div class="label">${label}</div><div class="value">${value}</div>${delta ? html`<div class="delta">${delta}</div>` : ''}</div>`;
}
const clip = (s, n) => (s.length > n ? s.slice(0, n).trimEnd() + '…' : s);

function commentCard(c, r) {
  const sc = Math.round(c.score * 100);
  return html`<article class="comment">
    <div>
      <div class="who">
        <b>${c.author || 'anonymous'}</b>
        ${c.authorIsUploader ? html`<span class="tag creator">creator</span>` : ''}
        ${c.isPinned ? html`<span class="tag"><span data-icon="pin"></span> pinned</span>` : ''}
        ${c.isHearted ? html`<span class="tag"><span data-icon="heart"></span> hearted</span>` : ''}
        ${c.isReply ? html`<span class="tag">reply</span>` : ''}
        ${c.repeats > 1 ? html`<span class="tag">×${c.repeats} repeated</span>` : ''}
        ${c.toxic != null && c.toxic >= (r.toxicity?.threshold ?? 0.7) ? html`<span class="tag toxic">toxic ${Math.round(c.toxic * 100)}%</span>` : ''}
        ${c.emotion && c.emotion !== 'neutral' ? html`<span class="tag">${c.emotion}</span>` : ''}
        <span>${c.langName}</span>
        ${c.publishedAt ? html`<span>${fmt.date(c.publishedAt)}</span>` : ''}
      </div>
      <div class="text">${c.text}</div>
    </div>
    <div class="side"><span class="score-pill ${scoreClass(sc)}">${c.sentiment} ${fmt.signed(sc)}</span><span class="likes">${fmt.count(c.likes)} likes</span></div>
  </article>`;
}

function renderAllComments(r) {
  const wrap = $('#all-comments');
  const rows = [...r.comments].sort((a, b) => b.likes - a.likes);
  setHTML(wrap, html`<table class="data"><thead><tr><th>Comment</th><th>Author</th><th>Lang</th><th>Sentiment</th><th class="num">Score</th><th class="num">Likes</th>${r.toxicity ? html`<th class="num">Toxic</th>` : ''}${r.emotions ? html`<th>Emotion</th>` : ''}<th>Theme</th></tr></thead>
    <tbody>${rows.map((c) => html`<tr><td>${clip(c.text, 240)}</td><td>${c.author || ''}</td><td>${c.lang}</td><td>${c.sentiment}</td><td class="num">${fmt.signed(c.score * 100)}</td><td class="num">${fmt.count(c.likes)}</td>${r.toxicity ? html`<td class="num">${c.toxic == null ? '' : Math.round(c.toxic * 100) + '%'}</td>` : ''}${r.emotions ? html`<td>${c.emotion || ''}</td>` : ''}<td>${c.theme == null ? '' : r.themes.find((t) => t.id === c.theme)?.label || ''}</td></tr>`)}</tbody></table>`);
}

// ── recent ───────────────────────────────────────────────────────────────
async function loadRecent() {
  try {
    const { reports, hidden } = await api.recent(12);
    const sec = $('#recent');
    if (hidden || !reports?.length) { sec.hidden = true; return; }
    sec.hidden = false;
    setHTML($('#recent-list'), html`${reports.map((m) => html`<a class="recent-card" href="/r/${m.id}" data-report="${m.id}">
      <img src="${m.thumbnail || `https://i.ytimg.com/vi/${m.videoId}/mqdefault.jpg`}" alt="" loading="lazy">
      <div><div class="t">${m.title || m.videoId}</div><div class="m"><span class="score-pill ${scoreClass(m.score)}">${fmt.signed(m.score)}</span><span>${verdictName(m.verdict)}</span><span>· ${fmt.count(m.analysed)} comments</span></div></div></a>`)}`);
    $('#recent-list').addEventListener('click', (e) => {
      const a = e.target.closest('a[data-report]');
      if (!a) return;
      e.preventDefault();
      openReport(a.dataset.report);
    }, { once: true });
  } catch { /* fine */ }
}
