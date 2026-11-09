// Clip editor: an embedded YouTube player, a timeline with YouTube's "most replayed" heatmap and
// chapter ticks, draggable range handles, and exact time inputs. Supports several ranges.
import { html, setHTML, fmtTime, parseTime, icon, clamp, toast } from './util.js';

let ytApiPromise = null;
function loadYouTubeApi() {
  if (window.YT?.Player) return Promise.resolve(window.YT);
  if (ytApiPromise) return ytApiPromise;
  ytApiPromise = new Promise((resolve, reject) => {
    const previous = window.onYouTubeIframeAPIReady;
    window.onYouTubeIframeAPIReady = () => { previous?.(); resolve(window.YT); };
    const s = document.createElement('script');
    s.src = 'https://www.youtube.com/iframe_api';
    s.async = true;
    s.onerror = () => { ytApiPromise = null; reject(new Error('The YouTube player could not be loaded.')); };
    document.head.append(s);
    setTimeout(() => reject(new Error('The YouTube player took too long to load.')), 15000);
  });
  return ytApiPromise;
}

const MIN_LEN = 0.5;
let seq = 0;

export class ClipEditor {
  /**
   * @param {HTMLElement} root
   * @param {{duration:number, chapters?:Array, heatmap?:Array, videoId?:string|null, thumbnail?:string,
   *          start?:number|null, end?:number|null, maxSections?:number, onChange?:Function}} o
   */
  constructor(root, o) {
    this.root = root;
    this.duration = Math.max(1, o.duration || 0);
    this.chapters = o.chapters || [];
    this.heatmap = o.heatmap || [];
    this.videoId = o.videoId || null;
    this.thumbnail = o.thumbnail || '';
    this.maxSections = o.maxSections || 10;
    this.onChange = o.onChange || (() => {});
    const start = clamp(o.start ?? 0, 0, this.duration - MIN_LEN);
    const defaultLen = Math.min(30, this.duration - start);
    const end = o.end != null && o.end > start ? Math.min(o.end, this.duration) : start + defaultLen;
    this.sections = [{ start, end }];
    this.active = 0;
    this.player = null;
    this.playerReady = false;
    this.previewEnd = null;
    this.timer = null;
    this.uid = `ytp-${++seq}`;
    this.mount();
  }

  getSections() {
    return this.sections.map((s) => ({
      start: Math.round(s.start * 100) / 100,
      end: s.end >= this.duration - 0.05 ? null : Math.round(s.end * 100) / 100,
    }));
  }

  totalLength() {
    return this.sections.reduce((t, s) => t + (s.end - s.start), 0);
  }

  destroy() {
    clearInterval(this.timer);
    try { this.player?.destroy?.(); } catch { /* ignore */ }
    this.player = null;
    this.root.innerHTML = '';
  }

  // ── DOM ────────────────────────────────────────────────────────────────
  mount() {
    setHTML(this.root, html`
      <div class="clip">
        ${this.videoId ? html`
          <div class="player-frame" data-el="frame">
            ${this.thumbnail ? html`<img class="poster" src="${this.thumbnail}" alt="">` : ''}
            <div class="player-msg" data-el="pmsg"><span class="spinner"></span></div>
            <div id="${this.uid}"></div>
          </div>` : ''}
        <div>
          <div class="timeline" data-el="tl" aria-label="Clip timeline">
            ${this.heatPath()}
            ${this.chapters.filter((c) => c.start > 0.5).map((c) => html`<span class="tick" style="left:${this.pct(c.start)}%" title="${fmtTime(c.start)} · ${c.title}"></span>`)}
            <div data-el="ranges"></div>
            <span class="playhead" data-el="ph" hidden></span>
          </div>
          <div class="timeline-scale"><span>0:00</span>
            ${this.heatmap.length ? html`<span class="heat-legend"><i></i>most replayed</span>` : ''}
            <span>${fmtTime(this.duration)}</span></div>
        </div>
        <div class="range-rows" data-el="rows"></div>
        <div class="clip-tools">
          <button type="button" class="ghost small" data-clip="add">${icon('plus')}Add another range</button>
          ${this.videoId ? html`<span class="opt-note">Tip: play the video, then use ${icon('stamp')} to set a cut at the current moment.</span>` : ''}
        </div>
      </div>`);
    this.el = {
      tl: this.root.querySelector('[data-el="tl"]'),
      ranges: this.root.querySelector('[data-el="ranges"]'),
      rows: this.root.querySelector('[data-el="rows"]'),
      ph: this.root.querySelector('[data-el="ph"]'),
      pmsg: this.root.querySelector('[data-el="pmsg"]'),
    };
    this.renderRanges();
    this.renderRows();
    this.bind();
    if (this.videoId) this.initPlayer();
  }

  heatPath() {
    if (!this.heatmap.length) return '';
    const pts = this.heatmap.map((p) => [((p.start + p.end) / 2 / this.duration) * 1000, 100 - clamp(p.value, 0, 1) * 92]);
    const d = `M0,100 L${pts.map(([x, y]) => `${x.toFixed(1)},${y.toFixed(1)}`).join(' L')} L1000,100 Z`;
    return html`<svg class="heat" viewBox="0 0 1000 100" preserveAspectRatio="none" aria-hidden="true"><path d="${d}"/></svg>`;
  }

  pct(t) { return clamp((t / this.duration) * 100, 0, 100); }

  renderRanges() {
    setHTML(this.el.ranges, html`${this.sections.map((s, i) => {
      const active = i === this.active;
      return html`<div class="range ${active ? 'active' : ''}" data-i="${i}" style="left:${this.pct(s.start)}%;width:${this.pct(s.end) - this.pct(s.start)}%">
        ${this.sections.length > 1 ? html`<span class="num">${i + 1}</span>` : ''}
      </div>
      ${active ? html`
        <span class="handle" data-edge="start" data-i="${i}" role="slider" tabindex="0" aria-label="Clip start"
              aria-valuemin="0" aria-valuemax="${Math.round(this.duration)}" aria-valuenow="${Math.round(s.start)}" aria-valuetext="${fmtTime(s.start, true)}"
              style="left:${this.pct(s.start)}%"><span class="lbl">${fmtTime(s.start, true)}</span></span>
        <span class="handle" data-edge="end" data-i="${i}" role="slider" tabindex="0" aria-label="Clip end"
              aria-valuemin="0" aria-valuemax="${Math.round(this.duration)}" aria-valuenow="${Math.round(s.end)}" aria-valuetext="${fmtTime(s.end, true)}"
              style="left:${this.pct(s.end)}%"><span class="lbl">${fmtTime(s.end, true)}</span></span>` : ''}`;
    })}`);
  }

  renderRows() {
    const many = this.sections.length > 1;
    const stamp = this.videoId
      ? (edge) => html`<button type="button" class="icon-btn small" data-clip="now" data-edge="${edge}" title="Set ${edge} to the player's current time" aria-label="Set ${edge} from player">${icon('stamp')}</button>`
      : () => '';
    setHTML(this.el.rows, html`${this.sections.map((s, i) => html`
      <div class="range-row ${i === this.active ? 'active' : ''}" data-i="${i}">
        ${many ? html`<span class="n">${i + 1}</span>` : ''}
        <span class="t"><input class="input" data-edge="start" value="${fmtTime(s.start, true)}" aria-label="Start time" inputmode="decimal">${stamp('start')}</span>
        <span class="arrow">→</span>
        <span class="t"><input class="input" data-edge="end" value="${fmtTime(s.end, true)}" aria-label="End time" inputmode="decimal">${stamp('end')}</span>
        <span class="len" title="Clip length">${fmtTime(s.end - s.start, true)}</span>
        ${this.videoId ? html`<button type="button" class="ghost small" data-clip="preview">${icon('play')}Preview</button>` : ''}
        ${many ? html`<button type="button" class="icon-btn small" data-clip="remove" aria-label="Remove range" title="Remove">${icon('trash')}</button>` : ''}
      </div>`)}`);
    const add = this.root.querySelector('[data-clip="add"]');
    if (add) add.hidden = this.sections.length >= this.maxSections;
  }

  changed() {
    this.renderRanges();
    this.renderRows();
    this.onChange(this);
  }

  /** Lightweight update while dragging: move the elements, don't rebuild the rows. */
  nudgeVisuals() {
    this.renderRanges();
    const s = this.sections[this.active];
    const row = this.el.rows.querySelector(`.range-row[data-i="${this.active}"]`);
    if (row) {
      row.querySelector('[data-edge="start"]').value = fmtTime(s.start, true);
      row.querySelector('[data-edge="end"]').value = fmtTime(s.end, true);
      row.querySelector('.len').textContent = fmtTime(s.end - s.start, true);
    }
  }

  // ── interaction ────────────────────────────────────────────────────────
  timeAt(clientX) {
    const r = this.el.tl.getBoundingClientRect();
    return clamp(((clientX - r.left) / r.width) * this.duration, 0, this.duration);
  }

  setEdge(i, edge, t) {
    const s = this.sections[i];
    if (edge === 'start') s.start = clamp(t, 0, s.end - MIN_LEN);
    else s.end = clamp(t, s.start + MIN_LEN, this.duration);
  }

  bind() {
    const tl = this.el.tl;
    let drag = null;

    tl.addEventListener('pointerdown', (e) => {
      if (e.button !== 0) return;
      const handle = e.target.closest('.handle');
      const range = e.target.closest('.range');
      const t = this.timeAt(e.clientX);
      if (handle) {
        drag = { kind: 'edge', edge: handle.dataset.edge, i: +handle.dataset.i };
      } else if (range) {
        const i = +range.dataset.i;
        if (i !== this.active) { this.active = i; this.changed(); }
        const s = this.sections[i];
        drag = { kind: 'move', i, offset: t - s.start, len: s.end - s.start };
      } else {
        // empty track: move the nearest edge of the active range here
        const s = this.sections[this.active];
        const edge = Math.abs(t - s.start) <= Math.abs(t - s.end) ? 'start' : 'end';
        this.setEdge(this.active, edge, t);
        drag = { kind: 'edge', edge, i: this.active };
        this.nudgeVisuals();
        this.seek(t);
      }
      tl.setPointerCapture(e.pointerId);
      e.preventDefault();
    });

    tl.addEventListener('pointermove', (e) => {
      if (!drag) return;
      const t = this.timeAt(e.clientX);
      if (drag.kind === 'edge') {
        this.setEdge(drag.i, drag.edge, t);
        this.seek(this.sections[drag.i][drag.edge], false);
      } else {
        const s = this.sections[drag.i];
        s.start = clamp(t - drag.offset, 0, this.duration - drag.len);
        s.end = s.start + drag.len;
      }
      this.nudgeVisuals();
    });

    const end = () => {
      if (!drag) return;
      const d = drag;
      drag = null;
      if (d.kind === 'edge') this.seek(this.sections[d.i][d.edge], true);
      this.changed();
    };
    tl.addEventListener('pointerup', end);
    tl.addEventListener('pointercancel', end);

    tl.addEventListener('keydown', (e) => {
      const h = e.target.closest('.handle');
      if (!h) return;
      const step = e.altKey ? 0.1 : e.shiftKey ? 5 : 1;
      const keys = { ArrowLeft: -step, ArrowDown: -step, ArrowRight: step, ArrowUp: step, PageDown: -10, PageUp: 10 };
      if (!(e.key in keys) && e.key !== 'Home' && e.key !== 'End') return;
      e.preventDefault();
      const i = +h.dataset.i, edge = h.dataset.edge, s = this.sections[i];
      const t = e.key === 'Home' ? 0 : e.key === 'End' ? this.duration : s[edge] + keys[e.key];
      this.setEdge(i, edge, t);
      this.changed();
      this.seek(this.sections[i][edge]);
      this.root.querySelector(`.handle[data-edge="${edge}"]`)?.focus();
    });

    this.root.addEventListener('click', (e) => {
      const b = e.target.closest('[data-clip]');
      const row = e.target.closest('.range-row');
      if (row && +row.dataset.i !== this.active && !e.target.closest('input')) {
        this.active = +row.dataset.i;
        this.changed();
      }
      if (!b) return;
      const i = row ? +row.dataset.i : this.active;
      switch (b.dataset.clip) {
        case 'add': this.addRange(); break;
        case 'remove': this.sections.splice(i, 1); this.active = Math.min(this.active, this.sections.length - 1); this.changed(); break;
        case 'now': this.fromPlayer(i, b.dataset.edge); break;
        case 'preview': this.preview(i); break;
      }
    });

    this.root.addEventListener('focusin', (e) => {
      const row = e.target.closest('.range-row');
      if (row && +row.dataset.i !== this.active && e.target.matches('input')) {
        this.active = +row.dataset.i;
        this.renderRanges();
        this.el.rows.querySelectorAll('.range-row').forEach((r) => r.classList.toggle('active', +r.dataset.i === this.active));
      }
    });

    const commit = (input) => {
      const row = input.closest('.range-row');
      const i = +row.dataset.i, edge = input.dataset.edge, s = this.sections[i];
      const t = parseTime(input.value);
      const ok = t != null && t >= 0 && t <= this.duration + 0.01 &&
        (edge === 'start' ? t <= s.end - MIN_LEN : t >= s.start + MIN_LEN);
      if (!ok) {
        input.classList.remove('invalid');
        void input.offsetWidth;
        input.classList.add('invalid');
        input.value = fmtTime(s[edge], true);
        toast(edge === 'start' ? 'The start must be before the end.' : `The end must be after the start and within ${fmtTime(this.duration)}.`, 'err', 3000);
        return;
      }
      input.classList.remove('invalid');
      if (Math.abs(s[edge] - t) < 0.001) return;
      s[edge] = Math.min(t, this.duration);
      this.changed();
      this.seek(s[edge]);
    };
    this.root.addEventListener('change', (e) => { if (e.target.matches('.range-row input')) commit(e.target); });
    this.root.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' && e.target.matches('.range-row input')) { e.preventDefault(); commit(e.target); }
    });
  }

  addRange() {
    if (this.sections.length >= this.maxSections) return;
    const last = this.sections[this.sections.length - 1];
    let start = Math.min(last.end + 1, this.duration - MIN_LEN);
    if (this.playerReady) start = clamp(this.player.getCurrentTime?.() ?? start, 0, this.duration - MIN_LEN);
    const end = Math.min(start + Math.min(30, this.duration / 10 || 30), this.duration);
    this.sections.push({ start, end: Math.max(end, start + MIN_LEN) });
    this.active = this.sections.length - 1;
    this.changed();
  }

  // ── player ─────────────────────────────────────────────────────────────
  async initPlayer() {
    try {
      const YT = await loadYouTubeApi();
      if (!this.root.isConnected) return;
      this.player = new YT.Player(this.uid, {
        videoId: this.videoId,
        host: 'https://www.youtube-nocookie.com',
        playerVars: { playsinline: 1, rel: 0, modestbranding: 1, start: Math.floor(this.sections[0].start) },
        events: {
          onReady: () => {
            this.playerReady = true;
            this.el.pmsg?.remove();
            this.root.querySelector('.poster')?.remove();
            this.el.ph.hidden = false;
            this.timer = setInterval(() => this.tick(), 200);
          },
          onError: () => this.playerFailed('This video can’t be played in an embedded player. You can still type the times.'),
        },
      });
    } catch (err) {
      this.playerFailed(err.message + ' You can still type the times.');
    }
  }

  playerFailed(msg) {
    if (!this.el.pmsg) return;
    setHTML(this.el.pmsg, html`<span>${msg}</span>`);
  }

  tick() {
    if (!this.playerReady) return;
    const t = this.player.getCurrentTime?.();
    if (t == null) return;
    this.el.ph.style.left = `${this.pct(t)}%`;
    if (this.previewEnd != null && t >= this.previewEnd) {
      this.player.pauseVideo();
      this.previewEnd = null;
    }
  }

  seek(t, allowAhead = true) {
    if (!this.playerReady) return;
    try { this.player.seekTo(t, allowAhead); } catch { /* ignore */ }
    this.el.ph.style.left = `${this.pct(t)}%`;
  }

  fromPlayer(i, edge) {
    if (!this.playerReady) { toast('The player is still loading.', 'info', 2500); return; }
    const t = this.player.getCurrentTime();
    const s = this.sections[i];
    if (edge === 'start' && t >= s.end - MIN_LEN) s.end = Math.min(this.duration, t + Math.max(MIN_LEN, s.end - s.start));
    if (edge === 'end' && t <= s.start + MIN_LEN) { toast('That moment is before the clip start.', 'err', 3000); return; }
    this.setEdge(i, edge, t);
    this.active = i;
    this.changed();
  }

  preview(i) {
    if (!this.playerReady) { toast('The player is still loading.', 'info', 2500); return; }
    const s = this.sections[i];
    this.player.seekTo(s.start, true);
    this.player.playVideo();
    this.previewEnd = s.end;
  }
}

export const describeSections = (sections, duration) =>
  sections.map((s) => `${fmtTime(s.start, true)}–${s.end == null ? fmtTime(duration) : fmtTime(s.end, true)}`).join(', ');
