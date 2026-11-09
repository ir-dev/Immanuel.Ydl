// ark-ydl front end: paste a link → see what it is → choose → download.
import {
  $, $$, html, setHTML, safeUrl, fmtTime, fmtBytes, fmtCount, fmtDate, plural, store, toast,
  copyText, icon, hydrateIcons, clamp,
} from './util.js';
import { api } from './api.js';
import { ClipEditor, describeSections } from './clip.js';
import { JobList, askNotificationPermission } from './jobs.js';

// ── constants ─────────────────────────────────────────────────────────────
const PREFS_KEY = 'ydl.prefs.v1';
const DEFAULTS = {
  mode: 'video', quality: 1080, codec: 'auto', container: 'mp4', hdr: false,
  audioFormat: 'mp3', bitrate: 0, squareCover: false, smartTags: true,
  precise: true, subsMode: 'none', subLangs: [],
  embedMetadata: true, embedThumbnail: true, embedChapters: true,
  sbAction: 'none', sbCats: ['sponsor', 'selfpromo', 'interaction'],
  fileName: 'title', numberFiles: true, autoSave: true,
};
const CODECS = [
  ['auto', 'Smart — H.264 when available (plays everywhere)'],
  ['best', 'Best efficiency — AV1 / VP9 (smaller files)'],
  ['h264', 'H.264 only — maximum compatibility'],
  ['vp9', 'VP9'],
  ['av1', 'AV1'],
];
const CONTAINERS = [['mp4', 'MP4'], ['mkv', 'MKV'], ['webm', 'WebM']];
const AUDIO_FORMATS = [
  ['mp3', 'MP3', 'plays everywhere'],
  ['m4a', 'M4A', 'AAC · Apple-friendly'],
  ['opus', 'Opus', 'smallest for the quality'],
  ['flac', 'FLAC', 'lossless container'],
  ['wav', 'WAV', 'uncompressed · for editing'],
  ['ogg', 'OGG', 'Vorbis'],
  ['best', 'Original', 'no conversion at all'],
];
const LOSSY = new Set(['mp3', 'm4a', 'opus', 'ogg']);
const BITRATES = [[0, 'Best (VBR)'], [320, '320 kbps'], [256, '256 kbps'], [192, '192 kbps'], [128, '128 kbps'], [96, '96 kbps']];
const PLAYLIST_QUALITIES = [0, 2160, 1440, 1080, 720, 480, 360];
const SB_CATS = {
  sponsor: 'Sponsors', selfpromo: 'Self-promotion', interaction: 'Like / subscribe reminders',
  intro: 'Intros', outro: 'Outros & end cards', preview: 'Previews & recaps', filler: 'Filler & tangents',
  music_offtopic: 'Non-music parts', hook: 'Hooks & teasers',
};
const FILENAMES = [['title', 'Title'], ['titleId', 'Title [video id]'], ['channelTitle', 'Channel - Title'], ['dateTitle', 'Upload date - Title']];
const THEMES = ['auto', 'dark', 'light'];

// ── state ─────────────────────────────────────────────────────────────────
const saved = store.get(PREFS_KEY, {});
const S = {
  sys: null,
  probe: null,
  input: '',
  sel: { ...DEFAULTS, ...(saved && typeof saved === 'object' ? pick(saved, Object.keys(DEFAULTS)) : {}) },
  part: 'whole',
  chapterSel: new Set(),
  subSel: new Set(),
  autoSel: new Set(),
  plSel: new Set(),
  plFilter: '',
  plLast: null,
  clip: null,
};
let probeSeq = 0;
let jobs;

function pick(o, keys) { return Object.fromEntries(keys.filter((k) => k in o).map((k) => [k, o[k]])); }
// A mode picked *for* the user (music → audio, no subtitles → video) is not their preference;
// remember the one they chose last instead.
let chosenMode = S.sel.mode;
const savePrefs = () => store.set(PREFS_KEY, { ...S.sel, mode: chosenMode });
const looksLikeLink = (t) => !!t && /(https?:\/\/\S+|youtu\.?be|^\s*[A-Za-z0-9_-]{11}\s*$|^\s*PL[A-Za-z0-9_-]{16,}\s*$)/i.test(t);

// ── boot ──────────────────────────────────────────────────────────────────
function init() {
  hydrateIcons();
  // A thumbnail that fails to load (blocked host, removed video) leaves the tinted box, not a broken-image glyph.
  document.addEventListener('error', (e) => {
    if (e.target instanceof HTMLImageElement) e.target.style.visibility = 'hidden';
  }, true);
  applyTheme(store.get('ydl.theme', 'auto'));
  jobs = new JobList({
    section: $('#downloads'), list: $('#job-list'), badge: $('#dl-badge'), button: $('#btn-downloads'),
    onRetry: (request, meta) => startDownload(request, meta),
  });
  jobs.restore();
  bindGlobal();
  bindResult();
  loadSystem();
  setupExtras();
  handleIncoming();
}

function handleIncoming() {
  const q = new URLSearchParams(location.search);
  const raw = q.get('url') || q.get('text') || q.get('title') || (q.get('v') ? `https://www.youtube.com/watch?v=${q.get('v')}` : null);
  if (!raw) { $('#url').focus({ preventScroll: true }); return; }
  history.replaceState(null, '', location.pathname);
  $('#url').value = raw.trim();
  updateClear();
  probe(raw.trim());
}

// ── system status ─────────────────────────────────────────────────────────
async function loadSystem() {
  try {
    S.sys = await api.system();
  } catch (err) {
    showBanner('err', `Can't reach the server: ${err.message}`);
    setTimeout(loadSystem, 8000);
    return;
  }
  renderSystem();
  if (S.sys.preparing) setTimeout(loadSystem, 3000);
}

function renderSystem() {
  const s = S.sys, t = s.tools;
  const pill = $('#sys-pill');
  pill.hidden = false;
  pill.className = 'pill' + (!t.ytDlp.ok ? ' err' : !t.ffmpeg.ok || !t.jsRuntime.ok ? ' warn' : '');
  pill.textContent = s.preparing ? 'starting…' : t.ytDlp.ok ? `yt-dlp ${t.ytDlp.version}` : 'engine missing';
  pill.title = [
    `yt-dlp: ${t.ytDlp.version || 'missing'}`,
    `ffmpeg: ${t.ffmpeg.version || 'missing'}`,
    `JS runtime: ${t.jsRuntime.ok ? `${t.jsRuntime.name} ${t.jsRuntime.version}` : 'missing'}`,
  ].join('\n');
  $('#app-version').textContent = s.version ? `v${s.version}` : '';

  if (s.preparing) showBanner('warn', 'Getting the download engine ready — on the first start this downloads yt-dlp. One moment…');
  else if (!t.ytDlp.ok) showBanner('err', `The download engine (yt-dlp) isn't available on this server. ${t.ytDlp.note || ''}`);
  else if (!t.ffmpeg.ok) showBanner('warn', `ffmpeg is missing on the server, so merging, clipping and audio conversion won't work. ${t.ffmpeg.note || ''}`);
  else if (!t.jsRuntime.ok) showBanner('warn', 'The server has no JavaScript runtime (deno or node ≥ 22). YouTube downloads may fail or be limited to low quality.');
  else $('#sys-banner').hidden = true;

  if (s.auth?.enabled && s.auth.user) {
    $('#user-box').hidden = false;
    $('#user-name').textContent = s.auth.user;
  }
}

function showBanner(kind, text) {
  const b = $('#sys-banner');
  b.className = `banner ${kind}`;
  setHTML(b, html`${icon(kind === 'err' ? 'warn' : 'info')}<p class="grow">${text}</p>`);
  b.hidden = false;
}

// ── probing ───────────────────────────────────────────────────────────────
async function probe(text = $('#url').value, playlist = null) {
  text = (text || '').trim();
  if (!text) { toast('Paste a YouTube link first.'); $('#url').focus(); return; }
  const my = ++probeSeq;
  destroyClip();
  S.probe = null;
  S.input = text;
  setHTML($('#result'), skeleton());
  const slow = setTimeout(() => {
    const n = $('#result .loading-note span:last-child');
    if (n) n.textContent = 'Still working — playlists and long videos take a little longer…';
  }, 7000);
  $('#btn-go').disabled = true;
  try {
    const p = await api.probe(text, playlist);
    if (my !== probeSeq) return;
    S.probe = p;
    renderResult();
    if (window.matchMedia('(max-width: 760px)').matches) $('#result').scrollIntoView({ behavior: 'smooth', block: 'start' });
  } catch (err) {
    if (my !== probeSeq) return;
    renderProbeError(err, () => probe(text, playlist));
  } finally {
    clearTimeout(slow);
    if (my === probeSeq) $('#btn-go').disabled = false;
  }
}

function skeleton() {
  return html`<article class="card skeleton" aria-busy="true">
    <div class="media"><div class="sk thumb-sk"></div>
      <div><div class="sk line" style="width:85%;height:22px"></div><div class="sk line" style="width:55%"></div><div class="sk line" style="width:35%"></div>
      <p class="loading-note"><span class="spinner"></span><span>Fetching the details from YouTube…</span></p></div></div>
  </article>`;
}

function renderProbeError(err, retry) {
  setHTML($('#result'), html`<article class="card error-card" role="alert">
    <div class="big-ico">${icon('warn')}</div>
    <div><h3>Couldn't load that link</h3><p>${err.message}</p>
      ${err.detail ? html`<details><summary class="link-btn">Technical details</summary><pre>${err.detail}</pre></details>` : ''}
      <button type="button" class="ghost small" data-act="retry-probe">${icon('retry')}Try again</button>
    </div></article>`);
  S.retryProbe = retry;
}

function destroyClip() {
  S.clip?.destroy();
  S.clip = null;
}

// ── result views ──────────────────────────────────────────────────────────
function renderResult() {
  const p = S.probe;
  destroyClip();
  if (p.kind === 'playlist') renderPlaylist(p);
  else renderVideo(p);
}

function renderVideo(p) {
  const live = p.liveStatus === 'is_live' || p.liveStatus === 'is_upcoming';
  S.part = !live && p.hintStart != null && p.duration ? 'clip' : 'whole';
  S.chapterSel = new Set(p.chapters.map((_, i) => i));
  S.subSel = new Set(p.subtitles.filter((s) => S.sel.subLangs.includes(s.lang)).map((s) => s.lang));
  S.autoSel = new Set();
  S.sel.mode = chosenMode;
  if (p.isMusic && S.sel.mode === 'video') S.sel.mode = 'audio';   // music links default to audio (not remembered)
  if (S.sel.mode === 'subtitles' && !hasSubs(p)) S.sel.mode = 'video';
  if (!p.videoQualities.length && S.sel.mode === 'video' && p.audioTracks.length) S.sel.mode = 'audio';

  setHTML($('#result'), html`
    ${mediaCard(p, live)}
    <section class="card options" id="opts" aria-label="Download options">
      <div class="opt-block">
        <div class="opt-head"><h3>Format</h3></div>
        <div class="seg wide" id="mode-seg"></div>
      </div>
      <div class="opt-block" id="opt-format"></div>
      <div class="opt-block" id="part-block" ${live ? 'hidden' : ''}>
        <div class="opt-head"><h3>What to download</h3><span class="opt-note" id="part-note"></span></div>
        <div class="seg" id="part-seg"></div>
        <div id="clip-wrap" hidden>
          <div id="clip-root"></div>
          <label class="toggle" style="margin-top:12px"><input type="checkbox" data-set="precise" ${S.sel.precise ? 'checked' : ''}>
            <span><b>Precise cuts</b><small>Re-encodes around the cut points so the clip starts exactly where you chose. Turn off for a faster, keyframe-aligned cut.</small></span></label>
        </div>
        <div id="chap-wrap" hidden></div>
      </div>
      <details class="more" id="opt-more"><summary><span>More options</span><span class="sum-note" id="more-note"></span></summary><div class="more-body" id="opt-more-body"></div></details>
      <div class="cta-bar"><button type="button" class="primary big" id="cta" data-act="download"></button><p class="cta-note" id="cta-note"></p></div>
    </section>`);
  refreshAll();
}

function renderPlaylist(p) {
  S.part = 'whole';
  S.plSel = new Set(p.entries.filter((e) => !e.unavailable).map((e) => e.index));
  S.plFilter = '';
  S.plLast = null;
  S.sel.mode = chosenMode;
  if (S.sel.mode === 'subtitles') S.sel.mode = 'video';
  if (p.isMusic && S.sel.mode === 'video') S.sel.mode = 'audio';
  const total = p.playlistCount ?? p.entries.length;
  setHTML($('#result'), html`
    <article class="card media-card">
      <div class="media">
        <a class="thumb" href="${safeUrl(p.webpageUrl)}" target="_blank" rel="noopener">
          ${p.thumbnail ? html`<img src="${p.thumbnail}" alt="">` : ''}
          <span class="dur">${icon('list')} ${total}</span>
        </a>
        <div class="info">
          <h2 class="title">${p.title || 'Playlist'}</h2>
          <div class="meta-line">
            ${p.uploader ? html`<span>${p.uploader}</span>` : ''}
            <span>${plural(total, 'video')}</span>
            ${p.duration ? html`<span>${fmtTime(p.duration)} total</span>` : ''}
          </div>
          ${p.truncated ? html`<div class="banner info">${icon('info')}<p class="grow">This server lists the first ${p.entries.length} of ${total} videos per download.</p></div>` : ''}
          ${p.description ? html`<details class="desc"><summary>Description</summary><p>${p.description}</p></details>` : ''}
          <div class="quick-actions">
            <button type="button" class="ghost small" data-act="copy">${icon('copy')}Copy link</button>
            <a class="ghost small" href="${safeUrl(p.webpageUrl)}" target="_blank" rel="noopener">${icon('external')}YouTube</a>
          </div>
        </div>
      </div>
    </article>
    <section class="card options" id="opts" aria-label="Download options">
      <div class="opt-block">
        <div class="opt-head"><h3>Format</h3></div>
        <div class="seg wide" id="mode-seg"></div>
      </div>
      <div class="opt-block" id="opt-format"></div>
      <div class="opt-block">
        <div class="opt-head"><h3>Videos</h3><span class="opt-note">Shift-click to select a range</span></div>
        <div class="pl-tools">
          <input class="input" id="pl-filter" type="search" placeholder="Filter by title…" aria-label="Filter videos">
          <button type="button" class="ghost small" data-act="pl-all">All</button>
          <button type="button" class="ghost small" data-act="pl-none">None</button>
          <span class="pl-count" id="pl-count"></span>
        </div>
        <div class="pl-list" id="pl-list"></div>
      </div>
      <details class="more" id="opt-more"><summary><span>More options</span><span class="sum-note" id="more-note"></span></summary><div class="more-body" id="opt-more-body"></div></details>
      <div class="cta-bar"><button type="button" class="primary big" id="cta" data-act="download"></button><p class="cta-note" id="cta-note"></p></div>
    </section>`);
  renderPlList();
  refreshAll();
}

function hasSubs(p) { return (p.subtitles?.length || 0) + (p.autoCaptions?.length || 0) > 0; }

function mediaCard(p, live) {
  return html`<article class="card media-card">
    <div class="media">
      <a class="thumb ${p.isShort ? 'short' : ''}" href="${safeUrl(p.webpageUrl)}" target="_blank" rel="noopener" title="Open on YouTube">
        ${p.thumbnail ? html`<img src="${p.thumbnail}" alt="">` : ''}
        ${p.duration ? html`<span class="dur">${fmtTime(p.duration)}</span>` : ''}
        ${p.isShort ? html`<span class="tag">Short</span>` : ''}
      </a>
      <div class="info">
        <h2 class="title">${p.title || 'Untitled'}</h2>
        <div class="meta-line">
          ${p.uploader ? (p.channelUrl ? html`<a href="${safeUrl(p.channelUrl)}" target="_blank" rel="noopener">${p.uploader}</a>` : html`<span>${p.uploader}</span>`) : ''}
          ${p.viewCount != null ? html`<span>${fmtCount(p.viewCount)} views</span>` : ''}
          ${p.uploadDate ? html`<span>${fmtDate(p.uploadDate)}</span>` : ''}
        </div>
        <div class="badges">${badges(p)}</div>
        ${p.description ? html`<details class="desc"><summary>Description</summary><p>${p.description}</p></details>` : ''}
        <div class="quick-actions">
          <button type="button" class="ghost small" data-act="thumb">${icon('image')}Thumbnail</button>
          <button type="button" class="ghost small" data-act="copy">${icon('copy')}Copy link</button>
          <a class="ghost small" href="${safeUrl(p.webpageUrl)}" target="_blank" rel="noopener">${icon('external')}YouTube</a>
        </div>
      </div>
    </div>
    ${p.listId ? html`<div class="banner info">${icon('list')}<p class="grow">This video is part of a playlist.</p><button type="button" class="ghost small" data-act="load-playlist">Show the whole playlist</button></div>` : ''}
    ${live ? html`<div class="banner warn">${icon('warn')}<p class="grow">${p.liveStatus === 'is_upcoming' ? "This premiere or live stream hasn't started yet." : "This is a live stream that's still running — downloads work once it has ended."}</p></div>` : ''}
    ${p.ageLimit >= 18 ? html`<div class="banner warn">${icon('warn')}<p class="grow">Age-restricted video — it downloads only if the server has signed-in YouTube cookies.</p></div>` : ''}
  </article>`;
}

function badges(p) {
  const out = [];
  const top = p.videoQualities?.[0];
  if (top) out.push(html`<span class="tagchip accent">${resName(top.res)}${top.fps > 30 ? Math.round(top.fps) : ''}</span>`);
  if (p.videoQualities?.some((q) => q.hdr)) out.push(html`<span class="tagchip accent">HDR</span>`);
  if (p.chapters?.length) out.push(html`<span class="tagchip">${icon('chapters')}${plural(p.chapters.length, 'chapter')}</span>`);
  if (p.subtitles?.length) out.push(html`<span class="tagchip">${icon('cc')}${plural(p.subtitles.length, 'subtitle')}</span>`);
  else if (p.autoCaptions?.length) out.push(html`<span class="tagchip">${icon('cc')}auto captions</span>`);
  if (p.isMusic || p.artist) out.push(html`<span class="tagchip">${icon('music')}${p.artist ? `${p.artist}${p.track ? ` — ${p.track}` : ''}` : 'Music'}</span>`);
  if (p.heatmap?.length) out.push(html`<span class="tagchip">${icon('sparkle')}most-replayed map</span>`);
  return out;
}

const resName = (r) => (r >= 4320 ? '8K' : r >= 2160 ? '4K' : `${r}p`);

// ── option panels ─────────────────────────────────────────────────────────
function refreshAll() {
  renderModeSeg();
  renderFormat();
  renderPart();
  renderMore();
  renderCta();
}

function renderModeSeg() {
  const p = S.probe;
  const modes = [['video', 'film', 'Video'], ['audio', 'music', 'Audio only']];
  if (p.kind !== 'playlist' && hasSubs(p)) modes.push(['subtitles', 'cc', 'Subtitles']);
  setHTML($('#mode-seg'), html`${modes.map(([v, ic, label]) => html`
    <button type="button" data-set="mode" data-val="${v}" aria-pressed="${S.sel.mode === v}">${icon(ic)}${label}</button>`)}`);
}

function renderFormat() {
  const box = $('#opt-format');
  if (!box) return;
  const p = S.probe, m = S.sel.mode;
  if (m === 'video') setHTML(box, videoPanel(p));
  else if (m === 'audio') setHTML(box, audioPanel(p));
  else setHTML(box, html`<div class="opt-head"><h3>Languages</h3><span class="opt-note">Saved as .srt files</span></div>${langPicker(p)}`);
}

function codecInUse() {
  const { codec, container } = S.sel;
  if (container === 'webm' && codec === 'auto') return 'best';
  if (container === 'webm' && codec === 'h264') return 'vp9';
  return codec;
}

function qualityList(p) {
  return p.kind === 'playlist'
    ? PLAYLIST_QUALITIES.filter((r) => r > 0).map((res) => ({ res, codecs: [], sizes: {} }))
    : p.videoQualities;
}

/** The row yt-dlp will actually pick for the current cap and codec preference. */
function effectiveQuality(p) {
  const list = qualityList(p);
  if (!list.length) return null;
  const codec = codecInUse();
  const fits = (q) => p.kind === 'playlist' || codec === 'auto' || codec === 'best' || q.codecs.includes(codec === 'av1' ? 'av1' : codec);
  const capped = S.sel.quality ? list.filter((q) => q.res <= S.sel.quality) : list;
  const pool = capped.length ? capped : [list[list.length - 1]];
  return pool.find(fits) || pool[0];
}

function videoBytes(p, q) {
  if (!q || !q.sizes) return null;
  const codec = codecInUse();
  const order = { auto: ['h264', 'vp9', 'av1'], best: ['av1', 'vp9', 'h264'], h264: ['h264'], vp9: ['vp9'], av1: ['av1'] }[codec] || [];
  let v = null;
  for (const c of order) if (q.sizes[c]) { v = q.sizes[c]; break; }
  if (v == null) {
    const vals = Object.values(q.sizes);
    v = vals.length ? Math.min(...vals) : null;
  }
  if (v == null) return null;
  const wantAac = codec === 'auto' || codec === 'h264';
  const a = p.audioTracks.find((t) => t.codec === (wantAac ? 'aac' : 'opus')) || p.audioTracks[0];
  return v + (a?.size || 0);
}

function audioBytes(p) {
  const d = p.duration;
  if (!d) return null;
  const f = S.sel.audioFormat, br = S.sel.bitrate;
  const native = (c) => p.audioTracks.find((t) => t.codec === c);
  if (!br && (f === 'm4a' || f === 'opus' || f === 'best')) {
    const t = native(f === 'm4a' ? 'aac' : 'opus') || p.audioTracks[0];
    if (t?.size) return t.size;
  }
  const kbps = { mp3: br || 245, m4a: br || 130, opus: br || 130, ogg: br || 192, flac: 850, wav: 1536, best: 130 }[f] || 160;
  return d * kbps * 125;
}

function videoPanel(p) {
  const list = qualityList(p);
  if (!list.length) return html`<p class="opt-note">No video streams were found for this link — try <button type="button" class="link-btn" data-set="mode" data-val="audio">Audio only</button>.</p>`;
  const eff = effectiveQuality(p);
  const codec = codecInUse();
  const best = list[0];
  const h264Max = p.kind === 'playlist' ? 1080 : Math.max(0, ...list.filter((q) => q.codecs.includes('h264')).map((q) => q.res));
  const hasHdr = p.kind !== 'playlist' && list.some((q) => q.hdr);
  const chip = (q, label, isBest) => {
    const pressed = isBest ? !S.sel.quality : !!S.sel.quality && eff && q.res === eff.res;
    const size = p.kind === 'playlist' ? null : videoBytes(p, q);
    const sub = [q.fps > 30 ? `${Math.round(q.fps)} fps` : '', size ? `≈ ${fmtBytes(size)}` : ''].filter(Boolean).join(' · ');
    return html`<button type="button" class="qchip" data-set="quality" data-type="int" data-val="${isBest ? 0 : q.res}" aria-pressed="${pressed}">
      <strong>${label}${!isBest && q.res >= 1440 ? html`<em>${q.res >= 4320 ? '8K' : q.res >= 2160 ? '4K' : '2K'}</em>` : ''}${!isBest && q.hdr ? html`<em>HDR</em>` : ''}</strong>
      <span>${isBest ? (p.kind === 'playlist' ? 'highest each video has' : `${resName(best.res)}${sub ? ' · ' + sub : ''}`) : sub || ' '}</span>
    </button>`;
  };
  const notes = [];
  if (codec === 'h264' && eff && h264Max && (S.sel.quality === 0 || S.sel.quality > h264Max) && best.res > h264Max)
    notes.push(`H.264 tops out at ${h264Max}p on YouTube, so you'll get ${h264Max}p. Choose "Smart" or "Best efficiency" for higher.`);
  if (codec === 'auto' && eff && eff.res > h264Max && h264Max > 0)
    notes.push(`Above ${h264Max}p YouTube only offers VP9/AV1 — fine on modern devices; older players may need VLC.`);
  return html`
    <div class="opt-head"><h3>Quality</h3>${eff ? html`<span class="opt-note">You'll get ${resName(eff.res)}${eff.fps > 30 ? ` ${Math.round(eff.fps)} fps` : ''}</span>` : ''}</div>
    <div class="quality-grid">
      ${chip(best, 'Best available', true)}
      ${list.map((q) => chip(q, `${q.res}p`, false))}
    </div>
    <div class="fields">
      <label class="field"><span>Video codec</span>
        <select data-set="codec">${CODECS.map(([v, label]) => html`<option value="${v}" ${S.sel.codec === v ? 'selected' : ''}>${label}</option>`)}</select></label>
      <div class="field"><span>Container</span>
        <div class="seg">${CONTAINERS.map(([v, label]) => html`<button type="button" data-set="container" data-val="${v}" aria-pressed="${S.sel.container === v}">${label}</button>`)}</div></div>
    </div>
    ${hasHdr ? html`<label class="toggle"><input type="checkbox" data-set="hdr" ${S.sel.hdr ? 'checked' : ''}>
      <span><b>Keep HDR</b><small>Only for HDR screens — on normal screens HDR video looks washed out.</small></span></label>` : ''}
    ${notes.map((n) => html`<p class="opt-note">${icon('info')} ${n}</p>`)}`;
}

function audioPanel(p) {
  const f = S.sel.audioFormat;
  const native = p.audioTracks?.[0];
  return html`
    <div class="opt-head"><h3>Audio format</h3>${native ? html`<span class="opt-note">Source: ${native.codec.toUpperCase()}${native.abr ? ` ${Math.round(native.abr)} kbps` : ''}</span>` : ''}</div>
    <div class="chips">${AUDIO_FORMATS.map(([v, label, hint]) => html`
      <button type="button" class="chip" data-set="audioFormat" data-val="${v}" aria-pressed="${f === v}">${label}<small>${hint}</small></button>`)}</div>
    ${LOSSY.has(f) ? html`
      <div class="opt-head"><h3>Bitrate</h3></div>
      <div class="chips">${BITRATES.map(([v, label]) => html`
        <button type="button" class="chip" data-set="bitrate" data-type="int" data-val="${v}" aria-pressed="${S.sel.bitrate === v}">${label}</button>`)}</div>
      ${S.sel.bitrate > 160 && native?.abr && native.abr < S.sel.bitrate ? html`<p class="opt-note">${icon('info')} The source is about ${Math.round(native.abr)} kbps — a higher bitrate makes the file bigger, not better.</p>` : ''}` : ''}
    ${f === 'flac' || f === 'wav' ? html`<p class="opt-note">${icon('info')} YouTube audio is lossy to begin with; ${f.toUpperCase()} keeps it exactly as-is for editing but won't add quality.</p>` : ''}
    <div class="toggles">
      <label class="toggle"><input type="checkbox" data-set="smartTags" ${S.sel.smartTags ? 'checked' : ''}>
        <span><b>Smart music tags</b><small>Reads "Artist - Title" from the video title; album and track number from a playlist.</small></span></label>
      ${f !== 'wav' ? html`<label class="toggle"><input type="checkbox" data-set="squareCover" ${S.sel.squareCover ? 'checked' : ''} ${S.sel.embedThumbnail ? '' : 'disabled'}>
        <span><b>Square cover art</b><small>Crops the thumbnail to 1:1, as music players expect.</small></span></label>` : ''}
    </div>`;
}

function renderPart() {
  const block = $('#part-block');
  if (!block) return;
  const p = S.probe;
  const showPart = S.sel.mode !== 'subtitles' && p.duration > 0;
  block.hidden = !showPart || p.liveStatus === 'is_live' || p.liveStatus === 'is_upcoming';
  if (block.hidden) { pauseClip(); return; }
  const parts = [['whole', 'film', 'Whole video'], ['clip', 'scissors', 'A clip']];
  if (p.chapters.length > 1) parts.push(['chapters', 'chapters', 'Chapters']);
  if (S.part === 'chapters' && p.chapters.length <= 1) S.part = 'whole';
  setHTML($('#part-seg'), html`${parts.map(([v, ic, label]) => html`
    <button type="button" data-set="part" data-val="${v}" aria-pressed="${S.part === v}">${icon(ic)}${label}</button>`)}`);

  $('#clip-wrap').hidden = S.part !== 'clip';
  $('#chap-wrap').hidden = S.part !== 'chapters';
  $('#part-note').textContent = S.part === 'whole' ? `${fmtTime(p.duration)} in total` : '';

  if (S.part === 'clip') {
    if (!S.clip) {
      S.clip = new ClipEditor($('#clip-root'), {
        duration: p.duration,
        chapters: p.chapters,
        heatmap: p.heatmap,
        videoId: p.isYouTube ? p.id : null,
        thumbnail: p.thumbnail,
        start: p.hintStart,
        end: p.hintEnd,
        maxSections: S.sys?.limits?.maxClipSections || 10,
        onChange: () => renderCta(),
      });
    }
  } else {
    pauseClip();
  }
  if (S.part === 'chapters') renderChapters();
}

function pauseClip() {
  try { S.clip?.player?.pauseVideo?.(); } catch { /* ignore */ }
}

function renderChapters() {
  const p = S.probe;
  const all = S.chapterSel.size === p.chapters.length;
  setHTML($('#chap-wrap'), html`
    <div class="pl-tools">
      <button type="button" class="ghost small" data-act="chap-all">All</button>
      <button type="button" class="ghost small" data-act="chap-none">None</button>
      <span class="pl-count">${S.chapterSel.size} of ${p.chapters.length} selected</span>
    </div>
    <div class="chapter-list">
      ${p.chapters.map((c, i) => html`<label class="chapter-row">
        <input type="checkbox" class="check" data-chapter="${i}" ${S.chapterSel.has(i) ? 'checked' : ''}>
        <span class="ts">${fmtTime(c.start)}</span><span class="ttl" title="${c.title}">${c.title}</span><span class="len">${fmtTime(c.end - c.start)}</span>
      </label>`)}
    </div>
    <p class="opt-note">${icon('info')} ${all ? 'Every chapter becomes its own file — great for albums and courses.' : 'Each selected chapter becomes its own file, named after the chapter.'}</p>`);
}

function langPicker(p) {
  const manual = p.subtitles || [];
  const auto = p.autoCaptions || [];
  const browser = (navigator.language || 'en').slice(0, 2).toLowerCase();
  const orig = auto.filter((a) => a.lang.endsWith('-orig'));
  const top = auto.filter((a) => orig.includes(a) || S.autoSel.has(a.lang) ||
    (!orig.length && (a.lang === 'en' || a.lang === browser)));
  const rest = auto.filter((a) => !top.includes(a));
  const chip = (s, set, kind) => html`<button type="button" class="chip" data-act="lang" data-kind="${kind}" data-lang="${s.lang}" aria-pressed="${set.has(s.lang)}">${s.name}${s.lang.endsWith('-orig') ? html`<small>original</small>` : ''}</button>`;
  return html`
    ${manual.length ? html`<div class="lang-group"><span>Uploaded by the creator</span><div class="lang-chips">${manual.map((s) => chip(s, S.subSel, 'manual'))}</div></div>` : ''}
    ${auto.length ? html`<div class="lang-group"><span>Auto-generated by YouTube</span>
      <div class="lang-chips">${top.map((s) => chip(s, S.autoSel, 'auto'))}</div>
      ${rest.length ? html`<select data-act="auto-add" aria-label="Add an auto-translated language"><option value="">+ Add an auto-translated language (${rest.length})…</option>${rest.map((s) => html`<option value="${s.lang}">${s.name}</option>`)}</select>` : ''}
    </div>` : ''}
    ${!manual.length && !auto.length ? html`<p class="opt-note">No subtitles for this video.</p>` : ''}`;
}

function renderMore() {
  const body = $('#opt-more-body');
  if (!body) return;
  const p = S.probe, m = S.sel.mode, isPl = p.kind === 'playlist';
  const media = m === 'video' || m === 'audio';
  const subsAvail = !isPl && hasSubs(p) && media;
  const sbAvail = media && p.isYouTube && S.part === 'whole';
  const subsModes = m === 'audio'
    ? [['none', 'None'], ['file', '.srt file']]
    : [['none', 'None'], ['embed', 'Embed in video'], ['file', '.srt file'], ['both', 'Both']];
  if (m === 'audio' && (S.sel.subsMode === 'embed' || S.sel.subsMode === 'both')) S.sel.subsMode = 'file';
  if (S.sel.container === 'webm' && m === 'video' && S.sel.subsMode !== 'none') { /* webm embeds WebVTT fine */ }

  const notes = [];
  if (subsAvail && S.sel.subsMode !== 'none' && S.subSel.size + S.autoSel.size) notes.push(`subtitles: ${[...S.subSel, ...S.autoSel].join(', ')}`);
  if (sbAvail && S.sel.sbAction !== 'none') notes.push('SponsorBlock on');
  if (S.sel.fileName !== 'title') notes.push(FILENAMES.find((f) => f[0] === S.sel.fileName)?.[1]);
  $('#more-note').textContent = notes.join(' · ');

  setHTML(body, html`
    ${subsAvail ? html`<div>
      <h4>${icon('cc')}Subtitles</h4>
      <div class="seg" style="margin-bottom:10px">${subsModes.map(([v, label]) => html`<button type="button" data-set="subsMode" data-val="${v}" aria-pressed="${S.sel.subsMode === v}">${label}</button>`)}</div>
      ${S.sel.subsMode !== 'none' ? langPicker(p) : ''}
    </div>` : ''}
    ${media ? html`<div>
      <h4>${icon('sparkle')}Embed in the file</h4>
      <div class="toggles">
        <label class="toggle"><input type="checkbox" data-set="embedMetadata" ${S.sel.embedMetadata ? 'checked' : ''}><span><b>Title, artist & date</b><small>Tags your player and library can read.</small></span></label>
        <label class="toggle"><input type="checkbox" data-set="embedThumbnail" ${S.sel.embedThumbnail ? 'checked' : ''} ${m === 'video' && S.sel.container === 'webm' ? 'disabled' : ''}><span><b>${m === 'audio' ? 'Cover art' : 'Thumbnail'}</b><small>${m === 'video' && S.sel.container === 'webm' ? 'WebM has no cover-art slot.' : 'Shown as the file’s artwork.'}</small></span></label>
        ${isPl || p.chapters?.length ? html`<label class="toggle"><input type="checkbox" data-set="embedChapters" ${S.sel.embedChapters ? 'checked' : ''}><span><b>Chapters</b><small>Jump between chapters in VLC, mpv, QuickTime…</small></span></label>` : ''}
      </div>
    </div>` : ''}
    ${sbAvail ? html`<div>
      <h4>${icon('skip')}SponsorBlock</h4>
      <div class="seg" style="margin-bottom:10px">
        ${[['none', 'Off'], ['remove', 'Cut them out'], ['mark', 'Mark as chapters']].map(([v, label]) => html`<button type="button" data-set="sbAction" data-val="${v}" aria-pressed="${S.sel.sbAction === v}">${label}</button>`)}
      </div>
      ${S.sel.sbAction !== 'none' ? html`<div class="chips">${Object.entries(SB_CATS).map(([v, label]) => html`
        <button type="button" class="chip" data-act="sbcat" data-val="${v}" aria-pressed="${S.sel.sbCats.includes(v)}">${label}</button>`)}</div>
        <p class="opt-note" style="margin-top:8px">Community-submitted segments from sponsor.ajay.app — not every video has them.</p>` : ''}
    </div>` : ''}
    <div class="fields">
      <label class="field"><span>File names</span>
        <select data-set="fileName">${FILENAMES.map(([v, label]) => html`<option value="${v}" ${S.sel.fileName === v ? 'selected' : ''}>${label}</option>`)}</select></label>
    </div>
    <div class="toggles">
      ${isPl ? html`<label class="toggle"><input type="checkbox" data-set="numberFiles" ${S.sel.numberFiles ? 'checked' : ''}><span><b>Number the files</b><small>"001 - Title", keeping the playlist order.</small></span></label>` : ''}
      <label class="toggle"><input type="checkbox" data-set="autoSave" ${S.sel.autoSave ? 'checked' : ''}><span><b>Save automatically when ready</b><small>Otherwise press Save in the downloads list.</small></span></label>
    </div>`);
}

function renderPlList() {
  const p = S.probe;
  const f = S.plFilter.toLowerCase();
  const rows = p.entries.filter((e) => !f || (e.title || '').toLowerCase().includes(f));
  setHTML($('#pl-list'), html`${rows.length ? rows.map((e) => html`
    <label class="pl-row ${e.unavailable ? 'off' : ''}" data-index="${e.index}">
      <input type="checkbox" class="check" data-pl="${e.index}" ${S.plSel.has(e.index) ? 'checked' : ''} ${e.unavailable ? 'disabled' : ''}>
      <span class="idx">${e.index}</span>
      ${e.thumbnail ? html`<img src="${e.thumbnail}" alt="" loading="lazy">` : html`<span></span>`}
      <span class="t"><b title="${e.title || ''}">${e.title || 'Untitled'}</b>
        <small>${e.unavailable ? 'unavailable' : [e.uploader, e.viewCount != null ? `${fmtCount(e.viewCount)} views` : '', e.liveStatus === 'is_live' ? 'LIVE' : ''].filter(Boolean).join(' · ')}</small></span>
      <span class="d">${e.duration ? fmtTime(e.duration) : ''}</span>
    </label>`) : html`<p class="opt-note" style="padding:12px">No videos match “${S.plFilter}”.</p>`}`);
  updatePlCount();
}

function updatePlCount() {
  const el = $('#pl-count');
  if (el) el.textContent = `${S.plSel.size} of ${S.probe.entries.length} selected`;
}

// ── call to action ────────────────────────────────────────────────────────
function ctaState() {
  const p = S.probe, m = S.sel.mode, isPl = p.kind === 'playlist';
  let label, size = null, disabled = false, note = '';
  const retention = S.sys?.limits?.retentionMinutes;
  note = `Prepared on the server, then saved to your device${retention ? ` · kept for ${retention >= 120 ? `${Math.round(retention / 60)} hours` : `${retention} min`}` : ''}.`;

  if (m === 'subtitles') {
    const n = S.subSel.size + S.autoSel.size;
    label = n ? `Download subtitles · ${plural(n, 'language')}` : 'Pick at least one language';
    disabled = n === 0;
    return { label, size, disabled, note };
  }

  let fmt;
  if (m === 'audio') {
    const name = AUDIO_FORMATS.find((x) => x[0] === S.sel.audioFormat)?.[1] || 'audio';
    fmt = `${name}${LOSSY.has(S.sel.audioFormat) && S.sel.bitrate ? ` · ${S.sel.bitrate} kbps` : ''}`;
    if (!isPl) size = audioBytes(p);
  } else {
    const eff = effectiveQuality(p);
    fmt = `${S.sel.container.toUpperCase()}${eff ? ` · ${isPl && !S.sel.quality ? 'best' : resName(eff.res)}` : ''}`;
    if (!isPl) size = videoBytes(p, eff);
  }

  if (isPl) {
    const n = S.plSel.size;
    label = n ? `Download ${plural(n, m === 'audio' ? 'track' : 'video')} · ${fmt}` : 'Select at least one video';
    disabled = n === 0;
    if (n > 1) note = 'Each file is downloaded on the server; save them one by one or all at once as a ZIP.';
  } else if (S.part === 'clip' && S.clip) {
    const len = S.clip.totalLength();
    const n = S.clip.sections.length;
    label = `Download ${n > 1 ? `${n} clips` : 'clip'} (${fmtTime(len, true)}) · ${fmt}`;
    if (size && p.duration) size = size * (len / p.duration);
  } else if (S.part === 'chapters') {
    const n = S.chapterSel.size;
    label = n ? `Download ${plural(n, 'chapter')} · ${fmt}` : 'Select at least one chapter';
    disabled = n === 0;
    if (size && p.duration) {
      const len = [...S.chapterSel].reduce((t, i) => t + (p.chapters[i].end - p.chapters[i].start), 0);
      size = size * (len / p.duration);
    }
  } else {
    label = `Download ${fmt}`;
  }
  if (S.sys && !S.sys.tools.ytDlp.ok && !S.sys.preparing) { disabled = true; note = 'The server is missing its download engine (yt-dlp).'; }
  return { label, size, disabled, note };
}

function renderCta() {
  const btn = $('#cta');
  if (!btn) return;
  const st = ctaState();
  setHTML(btn, html`${icon('download')}<span>${st.label}${st.size ? ` · ≈ ${fmtBytes(st.size)}` : ''}</span>`);
  btn.disabled = st.disabled;
  $('#cta-note').textContent = st.note;
}

// ── building the request ──────────────────────────────────────────────────
function buildRequest() {
  const p = S.probe, sel = S.sel, isPl = p.kind === 'playlist';
  const media = sel.mode === 'video' || sel.mode === 'audio';
  let clip = null;
  if (!isPl && media && S.part === 'clip' && S.clip) {
    clip = { sections: S.clip.getSections(), precise: sel.precise };
  } else if (!isPl && media && S.part === 'chapters') {
    const chosen = [...S.chapterSel].sort((a, b) => a - b).map((i) => p.chapters[i]);
    clip = chosen.length === p.chapters.length
      ? { sections: [], precise: sel.precise, splitChapters: true }
      : { sections: chosen.map((c) => ({ start: c.start, end: c.end, chapter: c.title })), precise: sel.precise };
  }
  const subsMode = sel.mode === 'subtitles' ? 'file' : media && !isPl ? sel.subsMode : 'none';
  const useSubs = subsMode !== 'none';
  return {
    url: p.url,
    playlist: isPl,
    mode: sel.mode,
    video: { maxHeight: sel.quality || null, codec: sel.codec, container: sel.container, hdr: !!sel.hdr },
    audio: { format: sel.audioFormat, bitrate: sel.bitrate || null, squareCover: !!sel.squareCover, smartTags: !!sel.smartTags },
    clip,
    subtitles: { mode: subsMode, langs: useSubs ? [...S.subSel] : [], autoLangs: useSubs ? [...S.autoSel] : [] },
    embed: { metadata: sel.embedMetadata, thumbnail: sel.embedThumbnail, chapters: sel.embedChapters },
    sponsorBlock: media && p.isYouTube && S.part === 'whole' && sel.sbAction !== 'none'
      ? { action: sel.sbAction, categories: sel.sbCats }
      : { action: 'none', categories: [] },
    playlistItems: isPl ? [...S.plSel].sort((a, b) => a - b) : null,
    fileName: sel.fileName,
    numberFiles: sel.numberFiles,
    title: p.title,
    thumbnail: p.thumbnail,
    label: jobLabel(clip),
  };
}

function jobLabel(clip) {
  const p = S.probe, m = S.sel.mode;
  if (m === 'subtitles') return `Subtitles · ${[...S.subSel, ...S.autoSel].join(', ')}`.slice(0, 110);
  let fmt;
  if (m === 'audio') {
    fmt = (AUDIO_FORMATS.find((x) => x[0] === S.sel.audioFormat)?.[1] || 'Audio') + (LOSSY.has(S.sel.audioFormat) && S.sel.bitrate ? ` · ${S.sel.bitrate} kbps` : '');
  } else {
    const eff = effectiveQuality(p);
    fmt = `${S.sel.container.toUpperCase()}${eff ? ` · ${p.kind === 'playlist' && !S.sel.quality ? 'best' : resName(eff.res)}` : ''}`;
  }
  if (p.kind === 'playlist') return `${plural(S.plSel.size, m === 'audio' ? 'track' : 'video')} · ${fmt}`;
  if (clip?.splitChapters) return `All chapters · ${fmt}`;
  if (clip?.sections?.[0]?.chapter) return `${plural(clip.sections.length, 'chapter')} · ${fmt}`;
  if (clip) return `Clip ${describeSections(clip.sections, p.duration)} · ${fmt}`.slice(0, 110);
  return fmt;
}

async function startDownload(request, meta) {
  askNotificationPermission();
  const btn = $('#cta');
  if (btn && meta.fromCta) btn.disabled = true;
  try {
    const snap = await api.createJob(request);
    jobs.add(snap, { title: meta.title, thumbnail: meta.thumbnail, label: meta.label, request, autoSave: S.sel.autoSave });
  } catch (err) {
    toast(err.message, 'err', 6000);
  } finally {
    if (btn && meta.fromCta) renderCta();
  }
}

// ── events ────────────────────────────────────────────────────────────────
function setSel(key, value) {
  if (key === 'part') {
    S.part = value;
    renderPart();
    renderMore();
    renderCta();
    return;
  }
  S.sel[key] = value;
  if (key === 'mode') chosenMode = value;
  if (key === 'subsMode' && value !== 'none') S.sel.subLangs = [...S.subSel];
  savePrefs();
  if (key === 'mode') { refreshAll(); return; }
  if (['quality', 'codec', 'container', 'hdr', 'audioFormat', 'bitrate', 'squareCover', 'smartTags'].includes(key)) renderFormat();
  if (key === 'embedThumbnail' && S.sel.mode === 'audio') renderFormat();
  if (key !== 'precise') renderMore();
  renderCta();
}

function bindResult() {
  const root = $('#result');

  root.addEventListener('click', (e) => {
    const setter = e.target.closest('[data-set]');
    if (setter && setter.tagName === 'BUTTON') {
      const t = setter.dataset.type;
      const v = setter.dataset.val;
      setSel(setter.dataset.set, t === 'int' ? parseInt(v, 10) : t === 'bool' ? v === 'true' : v);
      return;
    }
    const a = e.target.closest('[data-act]');
    if (!a) return;
    const p = S.probe;
    switch (a.dataset.act) {
      case 'download': {
        const req = buildRequest();
        startDownload(req, { title: p.title, thumbnail: p.thumbnail, label: req.label, fromCta: true });
        break;
      }
      case 'thumb':
        startDownload({ url: p.url, mode: 'thumbnail', title: p.title, thumbnail: p.thumbnail, label: 'Thumbnail · JPG' },
          { title: p.title, thumbnail: p.thumbnail, label: 'Thumbnail · JPG' });
        break;
      case 'copy':
        copyText(p.webpageUrl || p.url).then((ok) => toast(ok ? 'Link copied.' : "Couldn't copy — your browser blocked it.", ok ? 'ok' : 'err', 2500));
        break;
      case 'load-playlist': probe(S.input, true); break;
      case 'retry-probe': S.retryProbe?.(); break;
      case 'chap-all': S.chapterSel = new Set(p.chapters.map((_, i) => i)); renderChapters(); renderCta(); break;
      case 'chap-none': S.chapterSel.clear(); renderChapters(); renderCta(); break;
      case 'pl-all': visibleEntries().forEach((x) => S.plSel.add(x.index)); renderPlList(); renderCta(); break;
      case 'pl-none': visibleEntries().forEach((x) => S.plSel.delete(x.index)); renderPlList(); renderCta(); break;
      case 'lang': {
        const set = a.dataset.kind === 'auto' ? S.autoSel : S.subSel;
        const lang = a.dataset.lang;
        set.has(lang) ? set.delete(lang) : set.add(lang);
        if (a.dataset.kind === 'manual') { S.sel.subLangs = [...S.subSel]; savePrefs(); }
        a.setAttribute('aria-pressed', String(set.has(lang)));
        renderMoreNoteOnly();
        renderCta();
        break;
      }
      case 'sbcat': {
        const v = a.dataset.val;
        S.sel.sbCats = S.sel.sbCats.includes(v) ? S.sel.sbCats.filter((x) => x !== v) : [...S.sel.sbCats, v];
        if (!S.sel.sbCats.length) S.sel.sbCats = ['sponsor'];
        savePrefs();
        renderMore();
        break;
      }
    }
  });

  root.addEventListener('change', (e) => {
    const t = e.target;
    if (t.matches('select[data-set]')) { setSel(t.dataset.set, t.value); return; }
    if (t.matches('input[type="checkbox"][data-set]')) { setSel(t.dataset.set, t.checked); return; }
    if (t.matches('select[data-act="auto-add"]') && t.value) {
      S.autoSel.add(t.value);
      if (S.sel.mode === 'subtitles') renderFormat(); else renderMore();
      renderCta();
      return;
    }
    if (t.matches('input[data-chapter]')) {
      const i = +t.dataset.chapter;
      t.checked ? S.chapterSel.add(i) : S.chapterSel.delete(i);
      renderChapters();
      renderCta();
      return;
    }
    if (t.matches('input[data-pl]')) {
      const i = +t.dataset.pl;
      t.checked ? S.plSel.add(i) : S.plSel.delete(i);
      updatePlCount();
      renderCta();
    }
  });

  // Shift-click selects a range of playlist rows.
  root.addEventListener('click', (e) => {
    const box = e.target.closest('input[data-pl]');
    if (!box) return;
    const i = +box.dataset.pl;
    if (e.shiftKey && S.plLast != null && S.plLast !== i) {
      const idx = visibleEntries().map((x) => x.index);
      const [a, b] = [idx.indexOf(S.plLast), idx.indexOf(i)].sort((x, y) => x - y);
      if (a >= 0 && b >= 0) {
        const entries = visibleEntries().slice(a, b + 1).filter((x) => !x.unavailable);
        entries.forEach((x) => (box.checked ? S.plSel.add(x.index) : S.plSel.delete(x.index)));
        queueMicrotask(() => { renderPlList(); renderCta(); });
      }
    }
    S.plLast = i;
  });

  root.addEventListener('input', (e) => {
    if (e.target.id === 'pl-filter') {
      S.plFilter = e.target.value;
      renderPlList();
    }
  });
}

function renderMoreNoteOnly() {
  const notes = [];
  if (S.sel.subsMode !== 'none' && S.subSel.size + S.autoSel.size) notes.push(`subtitles: ${[...S.subSel, ...S.autoSel].join(', ')}`);
  if (S.sel.sbAction !== 'none' && S.part === 'whole') notes.push('SponsorBlock on');
  const n = $('#more-note');
  if (n) n.textContent = notes.join(' · ');
}

function visibleEntries() {
  const f = S.plFilter.toLowerCase();
  return S.probe.entries.filter((e) => !e.unavailable && (!f || (e.title || '').toLowerCase().includes(f)));
}

function updateClear() { $('#btn-clear').hidden = !$('#url').value; }

function bindGlobal() {
  const input = $('#url');
  $('#url-form').addEventListener('submit', (e) => { e.preventDefault(); probe(); });
  input.addEventListener('input', updateClear);
  input.addEventListener('paste', (e) => {
    const text = e.clipboardData?.getData('text') || '';
    if (!looksLikeLink(text)) return;
    e.preventDefault();
    input.value = text.trim();
    updateClear();
    probe(input.value);
  });
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') { input.value = ''; updateClear(); }
  });
  $('#btn-clear').addEventListener('click', () => { input.value = ''; updateClear(); input.focus(); });
  $('#btn-paste').addEventListener('click', async () => {
    try {
      const text = (await navigator.clipboard.readText()).trim();
      if (!text) { toast('Your clipboard is empty.'); return; }
      input.value = text;
      updateClear();
      probe(text);
    } catch {
      toast('Your browser blocked clipboard access — press Ctrl+V (⌘V on a Mac) instead.', 'info', 5000);
      input.focus();
    }
  });

  document.addEventListener('paste', (e) => {
    if (e.target.closest?.('input, textarea, select, [contenteditable="true"]')) return;
    const text = e.clipboardData?.getData('text') || '';
    if (!looksLikeLink(text)) return;
    e.preventDefault();
    input.value = text.trim();
    updateClear();
    window.scrollTo({ top: 0, behavior: 'smooth' });
    probe(input.value);
  });
  document.addEventListener('keydown', (e) => {
    if (e.key === '/' && !e.target.closest('input, textarea, select, [contenteditable="true"]')) {
      e.preventDefault();
      input.focus();
      input.select();
    }
  });
  document.addEventListener('dragover', (e) => {
    if ([...(e.dataTransfer?.types || [])].some((t) => t === 'text/uri-list' || t === 'text/plain')) e.preventDefault();
  });
  document.addEventListener('drop', (e) => {
    const text = e.dataTransfer?.getData('text/uri-list') || e.dataTransfer?.getData('text/plain') || '';
    if (!looksLikeLink(text)) return;
    e.preventDefault();
    input.value = text.split('\n')[0].trim();
    updateClear();
    probe(input.value);
  });

  $('#btn-theme').addEventListener('click', () => {
    const cur = store.get('ydl.theme', 'auto');
    const next = THEMES[(THEMES.indexOf(cur) + 1) % THEMES.length];
    applyTheme(next);
    store.set('ydl.theme', next);
    toast(`Theme: ${next === 'auto' ? 'follow the system' : next}`, 'info', 1800);
  });
  $('#btn-downloads').addEventListener('click', () => $('#downloads').scrollIntoView({ behavior: 'smooth' }));
  $('#btn-clear-done').addEventListener('click', () => jobs.clearFinished());
}

function applyTheme(t) {
  if (t === 'dark' || t === 'light') document.documentElement.dataset.theme = t;
  else delete document.documentElement.dataset.theme;
}

function setupExtras() {
  for (const el of $$('.host')) el.textContent = location.host;

  const bm = $('#bookmarklet');
  bm.href = `javascript:(()=>{location.href='${location.origin}/?url='+encodeURIComponent(location.href)})()`;
  bm.addEventListener('click', (e) => { e.preventDefault(); toast('Drag this button to your bookmarks bar, then click it on a YouTube page.', 'info', 5000); });

  let deferred = null;
  window.addEventListener('beforeinstallprompt', (e) => {
    e.preventDefault();
    deferred = e;
    $('#tip-install').hidden = false;
  });
  $('#btn-install').addEventListener('click', async () => {
    if (!deferred) return;
    deferred.prompt();
    await deferred.userChoice.catch(() => {});
    deferred = null;
    $('#tip-install').hidden = true;
  });

  if ('serviceWorker' in navigator && (location.protocol === 'https:' || location.hostname === 'localhost' || location.hostname === '127.0.0.1')) {
    navigator.serviceWorker.register('/sw.js').catch(() => {});
  }
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
else init();

// exposed for debugging in the console
window.arkYdl = { state: S, clamp };
