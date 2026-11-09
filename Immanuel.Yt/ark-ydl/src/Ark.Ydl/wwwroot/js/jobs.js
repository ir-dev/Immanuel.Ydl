// The downloads panel: one card per job, live over Server-Sent Events (polling as a fallback),
// remembered across reloads until the server expires the files.
import { api } from './api.js';
import { html, setHTML, fmtBytes, fmtSpeed, fmtEta, fmtClock, icon, store, toast, triggerDownload, plural } from './util.js';

const KEY = 'ydl.jobs.v1';
const TERMINAL = new Set(['completed', 'failed', 'canceled']);
const KIND_ICON = { video: 'film', audio: 'music', subtitle: 'cc', image: 'image', other: 'download' };
const BASE_TITLE = document.title;

export class JobList {
  /** @param {{section:HTMLElement, list:HTMLElement, badge:HTMLElement, button:HTMLElement, onRetry:Function}} o */
  constructor(o) {
    Object.assign(this, o);
    this.entries = new Map();
    this.list.addEventListener('click', (e) => this.onClick(e));
  }

  restore() {
    const saved = store.get(KEY, []);
    if (!Array.isArray(saved)) return;
    for (const meta of saved.slice(0, 30).reverse()) this.track(meta, null, false);
    this.refreshChrome();
  }

  /** A job the user just started. */
  add(snapshot, meta) {
    this.track({ ...meta, id: snapshot.id, createdAt: snapshot.createdAt }, snapshot, meta.autoSave);
    this.persist();
    this.refreshChrome();
    this.section.hidden = false;
    this.entries.get(snapshot.id)?.el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  }

  track(meta, snapshot, autoSave) {
    if (this.entries.has(meta.id)) return;
    const el = document.createElement('article');
    el.className = 'job';
    el.dataset.id = meta.id;
    setHTML(el, html`
      <img class="job-thumb" alt="" src="${meta.thumbnail || '/img/icon.svg'}" loading="lazy">
      <div class="job-body">
        <div class="job-top"><span class="job-title" data-f="title"></span><span class="job-label" data-f="label"></span></div>
        <div class="job-state"><span class="st" data-f="st"></span><span data-f="stage"></span></div>
        <div class="bar" data-f="bar"><i></i></div>
        <div class="job-meta" data-f="meta"></div>
        <div class="job-warn" data-f="warn" hidden></div>
        <div class="job-error" data-f="error" hidden></div>
        <div class="job-files" data-f="files" hidden></div>
        <div class="job-actions" data-f="actions"></div>
        <div class="job-expiry" data-f="expiry" hidden></div>
      </div>`);
    const f = Object.fromEntries([...el.querySelectorAll('[data-f]')].map((n) => [n.dataset.f, n]));
    const entry = { id: meta.id, meta, snap: snapshot, autoSave: !!autoSave, el, f, shownStatus: null, es: null, timer: null };
    this.entries.set(meta.id, entry);
    this.list.prepend(el);

    if (snapshot) {
      this.render(entry);
      if (!TERMINAL.has(snapshot.status)) this.subscribe(entry);
    } else {
      this.renderPlaceholder(entry);
      api.job(meta.id).then((s) => {
        this.update(entry, s, true);
        if (!TERMINAL.has(s.status)) this.subscribe(entry);
      }).catch((err) => {
        if (err.status === 410 || err.status === 404) this.forget(meta.id, false);
        else this.renderPlaceholder(entry, 'Could not check this download.');
      });
    }
  }

  subscribe(entry) {
    if (!('EventSource' in window)) { this.poll(entry); return; }
    const es = new EventSource(api.eventsUrl(entry.id));
    entry.es = es;
    let failures = 0;
    es.addEventListener('job', (ev) => {
      failures = 0;
      try { this.update(entry, JSON.parse(ev.data)); } catch { /* ignore a bad frame */ }
    });
    es.onerror = () => {
      if (entry.snap && TERMINAL.has(entry.snap.status)) { es.close(); return; }
      if (++failures >= 3) { es.close(); entry.es = null; this.poll(entry); }
    };
  }

  poll(entry) {
    const tick = async () => {
      try {
        const s = await api.job(entry.id);
        this.update(entry, s);
        if (!TERMINAL.has(s.status)) entry.timer = setTimeout(tick, 1500);
      } catch (err) {
        if (err.status === 410) this.forget(entry.id, false);
        else entry.timer = setTimeout(tick, 4000);
      }
    };
    tick();
  }

  update(entry, snap, restored = false) {
    const before = entry.snap?.status;
    entry.snap = snap;
    this.render(entry);
    if (TERMINAL.has(snap.status)) {
      entry.es?.close();
      entry.es = null;
      if (!restored && before && before !== snap.status) this.finished(entry);
    }
    this.refreshChrome();
  }

  finished(entry) {
    const s = entry.snap;
    const name = s.title || 'Your download';
    if (s.status === 'completed') {
      if (entry.autoSave) { entry.autoSave = false; this.save(entry); }
      notify('Download ready', name);
      toast(`Ready: ${name}`, 'ok', 3500);
    } else if (s.status === 'failed') {
      notify('Download failed', s.error || name);
      toast(s.error || 'The download failed.', 'err', 6000);
    }
  }

  save(entry) {
    const s = entry.snap;
    if (!s?.files?.length) return;
    triggerDownload(s.files.length === 1 ? s.files[0].url : s.zipUrl || s.files[0].url);
  }

  // ── rendering ──────────────────────────────────────────────────────────
  renderPlaceholder(entry, note = 'Checking…') {
    entry.f.title.textContent = entry.meta.title || 'Download';
    entry.f.label.textContent = entry.meta.label || '';
    entry.f.st.textContent = note;
    entry.f.bar.classList.add('indeterminate');
  }

  render(entry) {
    const s = entry.snap, f = entry.f, el = entry.el;
    el.classList.remove('queued', 'running', 'completed', 'failed', 'canceled');
    el.classList.add(s.status);
    f.title.textContent = s.title || entry.meta.title || 'Download';
    f.title.title = f.title.textContent;
    f.label.textContent = s.label || entry.meta.label || '';

    const item = s.itemCount > 1 ? ` · item ${s.itemIndex || 1} of ${s.itemCount}` : '';
    switch (s.status) {
      case 'queued':
        f.st.textContent = 'Waiting';
        f.stage.textContent = s.queuePosition ? `#${s.queuePosition} in the queue` : 'in the queue';
        break;
      case 'running':
        f.st.textContent = s.progress != null ? `${Math.floor(s.progress * 100)}%` : 'Working';
        f.stage.textContent = (s.stage || 'Downloading') + item;
        break;
      case 'completed':
        setHTML(f.st, html`${icon('check')}Ready`);
        f.stage.textContent = s.files?.length ? `${plural(s.files.length, 'file')} · ${fmtBytes(s.totalSize)}` : '';
        break;
      case 'failed':
        setHTML(f.st, html`${icon('warn')}Failed`);
        f.stage.textContent = '';
        break;
      case 'canceled':
        f.st.textContent = 'Canceled';
        f.stage.textContent = '';
        break;
    }

    const indeterminate = s.status === 'queued' || (s.status === 'running' && s.progress == null);
    f.bar.classList.toggle('indeterminate', indeterminate);
    f.bar.style.setProperty('--p', `${Math.round((s.status === 'completed' ? 1 : s.progress || 0) * 1000) / 10}%`);
    f.bar.setAttribute('role', 'progressbar');
    f.bar.setAttribute('aria-label', 'Download progress');
    if (!indeterminate) f.bar.setAttribute('aria-valuenow', String(Math.round((s.progress || 0) * 100)));

    const meta = [];
    if (s.status === 'running') {
      if (s.downloaded) meta.push(s.total ? `${fmtBytes(s.downloaded)} of ${fmtBytes(s.total)}` : fmtBytes(s.downloaded));
      if (s.speed) meta.push(fmtSpeed(s.speed));
      if (s.eta != null && s.progress != null) meta.push(fmtEta(s.eta));
    }
    f.meta.textContent = meta.join('   ');
    f.meta.hidden = meta.length === 0;

    f.warn.hidden = !(s.warnings?.length);
    if (s.warnings?.length) setHTML(f.warn, html`${icon('info')} ${s.warnings.join(' ')}`);

    // Rebuild the action / file areas only when the state changes, so an open preview survives updates.
    if (entry.shownStatus !== s.status) {
      entry.shownStatus = s.status;
      this.renderActions(entry);
      this.renderFiles(entry);
      this.renderError(entry);
    }

    f.expiry.hidden = !(TERMINAL.has(s.status) && s.status === 'completed' && s.expiresAt);
    if (!f.expiry.hidden) f.expiry.textContent = `Files stay available until ${fmtClock(s.expiresAt)}.`;
  }

  renderActions(entry) {
    const s = entry.snap;
    let h;
    if (s.status === 'queued' || s.status === 'running') {
      h = html`<button type="button" class="ghost small" data-job="cancel">${icon('x')}Cancel</button>`;
    } else if (s.status === 'completed') {
      const many = (s.files?.length || 0) > 1;
      h = html`
        <button type="button" class="primary small" data-job="save">${icon(many ? 'zip' : 'download')}${many ? 'Save all (.zip)' : 'Save'}</button>
        <button type="button" class="ghost small" data-job="remove">${icon('trash')}Remove</button>`;
    } else {
      h = html`
        ${entry.meta.request ? html`<button type="button" class="primary small" data-job="retry">${icon('retry')}Try again</button>` : ''}
        <button type="button" class="ghost small" data-job="remove">${icon('trash')}Remove</button>`;
    }
    setHTML(entry.f.actions, h);
  }

  renderFiles(entry) {
    const s = entry.snap;
    const files = s.status === 'completed' ? s.files || [] : [];
    entry.f.files.hidden = files.length === 0;
    setHTML(entry.f.files, html`${files.slice(0, 200).map((file) => html`
      <div class="job-file" data-index="${file.index}">
        <span class="k">${icon(KIND_ICON[file.kind] || 'download')}</span>
        <span class="nm" title="${file.name}">${file.name}</span>
        ${['video', 'audio', 'image'].includes(file.kind)
          ? html`<button type="button" class="icon-btn small" data-job="play" aria-label="Preview ${file.name}" title="Preview">${icon('play')}</button>`
          : html`<span></span>`}
        <a class="ghost small" href="${file.url}" download>${icon('download')}<span class="sz">${fmtBytes(file.size)}</span></a>
      </div>`)}`);
  }

  renderError(entry) {
    const s = entry.snap;
    entry.f.error.hidden = s.status !== 'failed';
    if (s.status !== 'failed') return;
    setHTML(entry.f.error, html`<div>${s.error || 'The download failed.'}</div>
      ${s.errorDetail ? html`<details><summary class="link-btn">Technical details</summary><pre>${s.errorDetail}</pre></details>` : ''}`);
  }

  togglePreview(entry, index) {
    const row = entry.f.files.querySelector(`.job-file[data-index="${index}"]`);
    if (!row) return;
    const open = row.querySelector('.job-preview');
    if (open) { open.remove(); return; }
    const file = entry.snap.files.find((x) => x.index === index);
    const box = document.createElement('div');
    box.className = 'job-preview';
    if (file.kind === 'video') setHTML(box, html`<video controls autoplay playsinline preload="metadata" src="${file.inlineUrl}"></video>`);
    else if (file.kind === 'audio') setHTML(box, html`<audio controls autoplay preload="metadata" src="${file.inlineUrl}"></audio>`);
    else setHTML(box, html`<img alt="${file.name}" src="${file.inlineUrl}">`);
    row.append(box);
    box.querySelector('video')?.addEventListener('error', () => {
      setHTML(box, html`<p class="opt-note">Your browser can't play this format inline — save it and open it in a media player.</p>`);
    });
  }

  async onClick(e) {
    const b = e.target.closest('[data-job]');
    if (!b) return;
    const card = b.closest('.job');
    const entry = this.entries.get(card.dataset.id);
    if (!entry) return;
    switch (b.dataset.job) {
      case 'cancel':
        b.disabled = true;
        try { this.update(entry, await api.cancel(entry.id)); } catch (err) { toast(err.message, 'err'); b.disabled = false; }
        break;
      case 'save': this.save(entry); break;
      case 'play': this.togglePreview(entry, +b.closest('.job-file').dataset.index); break;
      case 'remove': this.forget(entry.id, true); break;
      case 'retry':
        this.forget(entry.id, true);
        this.onRetry(entry.meta.request, entry.meta);
        break;
    }
  }

  forget(id, tellServer) {
    const entry = this.entries.get(id);
    if (!entry) return;
    entry.es?.close();
    clearTimeout(entry.timer);
    entry.el.remove();
    this.entries.delete(id);
    if (tellServer) api.remove(id).catch(() => {});
    this.persist();
    this.refreshChrome();
  }

  clearFinished() {
    for (const [id, entry] of [...this.entries]) {
      if (entry.snap && TERMINAL.has(entry.snap.status)) this.forget(id, entry.snap.status !== 'completed');
    }
  }

  persist() {
    // newest first; restore() replays them oldest first so the newest ends up on top again
    const list = [...this.entries.values()].reverse().map((e) => e.meta);
    store.set(KEY, list.slice(0, 30));
  }

  refreshChrome() {
    const all = [...this.entries.values()];
    const active = all.filter((e) => !e.snap || !TERMINAL.has(e.snap.status));
    this.section.hidden = all.length === 0;
    this.button.hidden = all.length === 0;
    this.badge.hidden = active.length === 0;
    this.badge.textContent = String(active.length);

    const running = active.filter((e) => e.snap?.status === 'running' && e.snap.progress != null);
    if (running.length) {
      const avg = running.reduce((t, e) => t + e.snap.progress, 0) / running.length;
      document.title = `${Math.floor(avg * 100)}% · ${BASE_TITLE}`;
    } else {
      document.title = active.length ? `⏳ ${BASE_TITLE}` : BASE_TITLE;
    }
  }
}

function notify(title, body) {
  if (!document.hidden || !('Notification' in window) || Notification.permission !== 'granted') return;
  try { new Notification(title, { body, icon: '/img/icon.svg', tag: 'ark-ydl' }); } catch { /* some browsers only allow SW notifications */ }
}

export function askNotificationPermission() {
  if (!('Notification' in window) || Notification.permission !== 'default') return;
  if (store.get('ydl.notifAsked', false)) return;
  store.set('ydl.notifAsked', true);
  Notification.requestPermission().catch(() => {});
}
