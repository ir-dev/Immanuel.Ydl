// Thin wrapper over the HTTP API. Errors surface as Error(message) with .status / .detail.
export const CLIENT_VERSION = '1.0.0';

let clientHeader = null;
export function clientInfo() {
  if (clientHeader) return clientHeader;
  const p = new URLSearchParams();
  try { const tz = Intl.DateTimeFormat().resolvedOptions().timeZone; if (tz) p.set('tz', tz); } catch { /* old browser */ }
  try {
    p.set('sw', String(Math.round(screen.width)));
    p.set('sh', String(Math.round(screen.height)));
    p.set('dpr', String(Math.round((window.devicePixelRatio || 1) * 100) / 100));
  } catch { /* no screen */ }
  const standalone = window.matchMedia?.('(display-mode: standalone)').matches || navigator.standalone === true;
  p.set('pwa', standalone ? '1' : '0');
  p.set('v', CLIENT_VERSION);
  clientHeader = p.toString();
  return clientHeader;
}

export async function request(method, url, body) {
  let res;
  try {
    const headers = { 'X-Ark-Client': clientInfo() };
    if (body) headers['Content-Type'] = 'application/json';
    res = await fetch(url, { method, credentials: 'same-origin', headers, body: body ? JSON.stringify(body) : undefined });
  } catch {
    const e = new Error("Can't reach the server. Check your connection and try again.");
    e.status = 0;
    throw e;
  }
  if (res.redirected && new URL(res.url).origin !== location.origin) {
    location.reload();
    throw new Error('Signing you in again…');
  }
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  if (!res.ok) {
    const e = new Error(data?.error || data?.title || `The server answered ${res.status}.`);
    e.status = res.status;
    e.detail = data?.detail;
    e.retryAfter = res.headers.get('Retry-After');
    throw e;
  }
  return data;
}

export const api = {
  system: () => request('GET', '/api/system'),
  profiles: () => request('GET', '/api/profiles'),
  analyse: (url, profile, fresh = false) => request('POST', '/api/analyses', { url, profile, fresh }),
  job: (id) => request('GET', `/api/analyses/${encodeURIComponent(id)}`),
  cancel: (id) => request('DELETE', `/api/analyses/${encodeURIComponent(id)}`),
  report: (id) => request('GET', `/api/reports/${encodeURIComponent(id)}`),
  recent: (count = 12) => request('GET', `/api/reports?count=${count}`),
  feedback: (id, liked, note) => request('POST', `/api/reports/${encodeURIComponent(id)}/feedback`, { liked, note }),
  admin: {
    session: () => request('GET', '/api/admin/session'),
    signIn: (key) => request('POST', '/api/admin/session', { key }),
    signOut: () => request('DELETE', '/api/admin/session'),
    overview: (q) => request('GET', `/api/admin/overview?${q}`),
    breakdowns: (q) => request('GET', `/api/admin/breakdowns?${q}`),
    analyses: (q) => request('GET', `/api/admin/analyses?${q}`),
    analysis: (id, q = '') => request('GET', `/api/admin/analyses/${encodeURIComponent(id)}?${q}`),
    requests: (q) => request('GET', `/api/admin/requests?${q}`),
    store: () => request('GET', '/api/admin/store'),
    status: () => request('GET', '/api/admin/status'),
    unload: () => request('POST', '/api/admin/models/unload'),
    download: (role) => request('POST', `/api/admin/models/${encodeURIComponent(role)}/download`),
    updateTool: () => request('POST', '/api/admin/tools/update'),
    refresh: () => request('POST', '/api/admin/audit/refresh'),
  },
};

/** Live job events over SSE with a polling fallback. onEvent(snapshot); resolves with the final snapshot. */
export function watchJob(id, onEvent, { signal } = {}) {
  return new Promise((resolve, reject) => {
    let done = false;
    const finish = (snap) => { if (done) return; done = true; es?.close(); clearInterval(poll); resolve(snap); };
    const fail = (e) => { if (done) return; done = true; es?.close(); clearInterval(poll); reject(e); };
    let es = null;
    let poll = null;
    const handle = (snap) => {
      onEvent(snap);
      if (snap.state === 'completed' || snap.state === 'failed' || snap.state === 'canceled') finish(snap);
    };
    const startPolling = () => {
      es?.close();
      es = null;
      if (poll) return;
      poll = setInterval(async () => {
        try { handle(await api.job(id)); } catch (e) { if (e.status === 404) fail(e); }
      }, 1500);
    };
    if ('EventSource' in window) {
      es = new EventSource(`/api/analyses/${encodeURIComponent(id)}/events`);
      es.onmessage = (m) => { try { handle(JSON.parse(m.data)); } catch { /* ignore a bad frame */ } };
      es.onerror = () => { if (!done) startPolling(); };
    } else startPolling();
    signal?.addEventListener('abort', () => fail(new DOMException('aborted', 'AbortError')));
  });
}
