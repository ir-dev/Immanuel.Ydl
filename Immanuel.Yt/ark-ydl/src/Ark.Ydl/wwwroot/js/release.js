// Release info: the version chip in the footer, the "About this release" dialog (versions, what's new, how to upgrade
// from any earlier version) and a one-time "updated" toast. The chip needs only /api/system's small release block;
// release notes are fetched when the dialog first opens.
import { $, html, setHTML, fmtDate, icon, store, toast } from './util.js';
import { api } from './api.js';
import { CLIENT_VERSION } from './version.js';

const SEEN_KEY = 'ydl.seenVersion';

export function cmpVersion(a, b) {
  const pa = String(a || '').replace(/^v/i, '').split(/[.+-]/).map((x) => parseInt(x, 10) || 0);
  const pb = String(b || '').replace(/^v/i, '').split(/[.+-]/).map((x) => parseInt(x, 10) || 0);
  for (let i = 0; i < 3; i++) if ((pa[i] || 0) !== (pb[i] || 0)) return (pa[i] || 0) < (pb[i] || 0) ? -1 : 1;
  return 0;
}

const COMPAT = {
  compatible: ['Drop-in', 'ok'],
  config: ['Config changes', 'warn'],
  breaking: ['Breaking', 'err'],
};
export const compatBadge = (level) => {
  const [label, cls] = COMPAT[level] || COMPAT.compatible;
  return html`<span class="compat ${cls}">${icon(cls === 'ok' ? 'check' : 'warn')}${label}</span>`;
};
const AREA = { api: 'HTTP API', config: 'Configuration', data: 'Data', deployment: 'Deployment', clients: 'Browsers & installed apps' };
const AUDIENCE = { operator: 'For whoever runs the server', user: 'For people using the page', api: 'For scripts calling the API' };

function ago(iso) {
  if (!iso) return '';
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (!isFinite(s)) return '';
  if (s < 90) return 'just now';
  if (s < 5400) return `${Math.round(s / 60)} min ago`;
  if (s < 86400 * 2) return `${Math.round(s / 3600)} h ago`;
  return `${Math.round(s / 86400)} days ago`;
}

export class ReleaseInfo {
  constructor({ chip, dialog, body, banner }) {
    this.chip = chip;
    this.dialog = dialog;
    this.body = body;
    this.banner = banner;
    this.rel = null;
    this.notes = null;
    chip.addEventListener('click', () => this.open());
    dialog.addEventListener('click', (e) => {
      if (e.target === dialog) dialog.close(); // a click on the backdrop
      if (e.target.closest('[data-act="reload"]')) location.reload();
    });
    dialog.addEventListener('change', (e) => {
      if (e.target.id === 'rel-from') this.plan(e.target.value);
    });
  }

  /** Called with /api/system's `release` block on every status load. */
  update(rel) {
    if (!rel) return;
    this.rel = rel;
    const stale = cmpVersion(CLIENT_VERSION, rel.version) !== 0;
    const state = stale ? 'stale' : rel.updateAvailable ? 'update' : rel.manifestMatches === false ? 'warn' : 'ok';
    this.chip.hidden = false;
    this.chip.className = `ver-chip ${state}`;
    const text = stale ? `v${CLIENT_VERSION} · reload for v${rel.version}`
      : rel.updateAvailable ? `v${rel.version} · v${rel.latest.version} available`
        : `v${rel.version}`;
    setHTML(this.chip, html`<span class="dot" aria-hidden="true"></span><span>${text}</span>`);
    this.chip.title = [
      `This app: v${rel.version}${rel.revision ? ` (${rel.revision})` : ''}`,
      `Web manifest: ${rel.manifest ? `v${rel.manifest}` : 'no version'}`,
      `Latest release: v${rel.latest.version}${rel.updateAvailable ? ' — update available' : ' — up to date'}`,
      stale ? `This page: v${CLIENT_VERSION} — reload to update` : '',
    ].filter(Boolean).join('\n');
    this.chip.setAttribute('aria-label', `Release information: ${text}`);

    if (stale && this.banner) {
      this.banner.hidden = false;
      setHTML(this.banner, html`${icon('info')}<p class="grow">A newer version of this page is available (v${rel.version}; you have v${CLIENT_VERSION}).</p>
        <button type="button" class="ghost small" data-act="reload">${icon('retry')}Reload</button>`);
      this.banner.querySelector('[data-act="reload"]').addEventListener('click', () => location.reload(), { once: true });
    }

    const seen = store.get(SEEN_KEY, null);
    if (!stale) {
      if (seen && cmpVersion(seen, rel.version) < 0) {
        toast(`ARK-RAPID was updated to v${rel.version}.`, 'info', 10000, { label: "What's new", run: () => this.open(seen) });
      }
      if (seen !== rel.version) store.set(SEEN_KEY, rel.version);
      this.previous = seen && cmpVersion(seen, rel.version) < 0 ? seen : null;
    }
  }

  async open(fromVersion = null) {
    if (!this.dialog.open) this.dialog.showModal();
    if (!this.notes) {
      setHTML(this.body, html`<p class="loading-note"><span class="spinner"></span><span>Loading the release notes…</span></p>`);
      try {
        this.notes = await api.releases();
      } catch (err) {
        setHTML(this.body, html`<div class="banner err">${icon('warn')}<p class="grow">${err.message}</p></div>`);
        return;
      }
    }
    this.render(fromVersion || this.previous);
  }

  render(fromVersion) {
    const rel = this.rel || {};
    const n = this.notes;
    const current = n.releases.find((r) => cmpVersion(r.version, n.current) === 0) || n.releases[0];
    const stale = cmpVersion(CLIENT_VERSION, rel.version) !== 0;
    const older = n.releases.filter((r) => cmpVersion(r.version, n.current) < 0);
    const fromOptions = [
      ...older.map((r) => [r.version, `v${r.version} — ${r.title}`]),
      ...n.legacy.map((l) => [l.id, `${l.name} (replaced app)`]),
    ];
    const pick = fromOptions.some(([v]) => v === fromVersion) ? fromVersion : fromOptions[0]?.[0];
    const latest = rel.latest || {};

    setHTML(this.body, html`
      <div class="rel-versions">
        <div class="rel-v">
          <span class="k">This app</span><b>v${rel.version || n.current}</b>
          <small>${rel.revision ? `build ${rel.revision}` : ''}${rel.revision && rel.builtAt ? ' · ' : ''}${rel.builtAt ? `built ${fmtDate(rel.builtAt.slice(0, 10))}` : ''}</small>
        </div>
        <div class="rel-v">
          <span class="k">Web manifest</span><b>${rel.manifest ? `v${rel.manifest}` : '—'}</b>
          <small>${rel.manifestMatches === false ? html`${icon('warn')} differs from the app` : html`${icon('check')} matches the app`}</small>
        </div>
        <div class="rel-v ${rel.updateAvailable ? 'is-update' : ''}">
          <span class="k">Latest release</span><b>v${latest.version || n.latest}</b>
          <small>${rel.updateAvailable ? html`${icon('arrow')} update available` : html`${icon('check')} up to date`}
            · ${latest.source === 'feed' ? `release feed, checked ${ago(latest.checkedAt)}` : "this build's own notes"}</small>
        </div>
      </div>
      ${stale ? html`<div class="banner info">${icon('info')}<p class="grow">This page is v${CLIENT_VERSION}, the server runs v${rel.version}.</p><button type="button" class="ghost small" data-act="reload">${icon('retry')}Reload</button></div>` : ''}
      ${latest.error && latest.source !== 'feed' ? html`<p class="opt-note">${icon('info')} ${latest.error}</p>` : ''}

      ${n.newer?.length ? html`<section class="rel-sec rel-newer">
        <h3>${icon('arrow')}What updating brings</h3>
        ${n.newer.map((r) => html`<div class="rel-mini"><div class="rel-head"><b>v${r.version} · ${r.title}</b>${compatBadge(r.compatibility?.level)}</div>${r.summary ? html`<p class="muted">${r.summary}</p>` : ''}</div>`)}
      </section>` : ''}

      <section class="rel-sec">
        <div class="rel-head"><h3>v${current.version} · ${current.title}</h3>${compatBadge(current.compatibility?.level)}</div>
        <p class="muted small">${current.date ? fmtDate(current.date) : ''}</p>
        ${current.summary ? html`<p>${current.summary}</p>` : ''}
        ${changes(current.changes)}
      </section>

      ${fromOptions.length ? html`<section class="rel-sec">
        <h3>Upgrading from an earlier version?</h3>
        <label class="field rel-from"><span>Coming from</span>
          <select id="rel-from">${fromOptions.map(([v, label]) => html`<option value="${v}" ${v === pick ? 'selected' : ''}>${label}</option>`)}</select></label>
        <div id="rel-plan" aria-live="polite"></div>
      </section>` : ''}

      ${n.matrix?.length ? html`<details class="rel-sec">
        <summary>Compatibility of every earlier version with v${n.current}</summary>
        <div class="table-wrap"><table class="rel-matrix">
          <thead><tr><th scope="col">From</th><th scope="col">Compatibility</th><th scope="col">Steps</th><th scope="col">Recommendation</th></tr></thead>
          <tbody>${n.matrix.map((m) => html`<tr><th scope="row">${m.from.startsWith('legacy') ? m.name : `v${m.from}`}</th><td>${compatBadge(m.level)}</td>
            <td class="num">${m.steps}${m.requiredSteps ? ` (${m.requiredSteps} required)` : ''}</td><td>${m.recommendation}</td></tr>`)}</tbody>
        </table></div>
      </details>` : ''}

      <details class="rel-sec">
        <summary>All releases</summary>
        ${n.releases.map((r) => html`<div class="rel-mini">
          <div class="rel-head"><b>v${r.version} · ${r.title}</b>${compatBadge(r.compatibility?.level)}</div>
          <p class="muted small">${r.date ? fmtDate(r.date) : ''}</p>${r.summary ? html`<p>${r.summary}</p>` : ''}${changes(r.changes)}
        </div>`)}
        ${n.legacy.map((l) => html`<div class="rel-mini"><b>${l.name}</b><p class="muted small">${l.stack}</p><p>${l.summary}</p></div>`)}
      </details>`);
    if (pick) this.plan(pick);
  }

  async plan(from) {
    const box = $('#rel-plan', this.body);
    if (!box) return;
    box.classList.add('dim');
    let p;
    try {
      p = await api.migration(from);
    } catch (err) {
      setHTML(box, html`<p class="opt-note">${icon('warn')} ${err.message}</p>`);
      box.classList.remove('dim');
      return;
    }
    setHTML(box, planHtml(p));
    box.classList.remove('dim');
  }
}

/** A migration plan (GET /api/releases/migration) as markup: verdict, path, per-area compatibility, steps by audience. */
export function planHtml(p) {
  const groups = {};
  for (const s of p.steps) (groups[s.audience] ||= []).push(s);
  return html`
    <div class="plan">
      <p class="plan-rec">${compatBadge(p.level)} <span>${p.recommendation}</span></p>
      ${p.path?.length ? html`<p class="muted small">${p.fromName ? `${p.fromName} → ` : `v${p.from} → `}${p.path.map((v) => `v${v}`).join(' → ')}</p>` : ''}
      ${p.areas?.length ? html`<div class="table-wrap"><table class="plan-areas"><tbody>${p.areas.map((a) => html`<tr>
        <th scope="row">${AREA[a.area] || a.area}</th><td>${compatBadge(a.level)}</td><td>${a.notes.map((x) => html`<p>${x}</p>`)}</td></tr>`)}</tbody></table></div>` : ''}
      ${Object.entries(groups).map(([aud, steps]) => html`<h4>${AUDIENCE[aud] || aud}</h4>
        <ol class="plan-steps">${steps.map((s) => html`<li>
          <b>${s.title}</b>${s.required ? html` <span class="req">required</span>` : ''}
          ${s.detail ? html`<p>${s.detail}</p>` : ''}<small class="muted">from v${s.version}</small></li>`)}</ol>`)}
    </div>`;
}

export function changes(c) {
  if (!c) return '';
  const rows = [['added', 'New'], ['changed', 'Changed'], ['fixed', 'Fixed'], ['removed', 'Removed'], ['security', 'Security']]
    .filter(([k]) => c[k]?.length);
  return html`${rows.map(([k, label]) => html`<h4>${label}</h4><ul class="rel-list">${c[k].map((x) => html`<li>${x}</li>`)}</ul>`)}`;
}
