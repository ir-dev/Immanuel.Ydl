// Thin wrapper over the ark-ydl HTTP API. Errors surface as Error(message) with .status / .detail.
import { CLIENT_VERSION } from './version.js';

// What the page tells the server about itself, for the audit log: time zone, screen, installed-app or tab, page version.
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
    res = await fetch(url, {
      method,
      credentials: 'same-origin',
      headers,
      body: body ? JSON.stringify(body) : undefined,
    });
  } catch {
    const e = new Error("Can't reach the server. Check your connection and try again.");
    e.status = 0;
    throw e;
  }
  // A sign-in session that expired answers with a redirect to the identity provider.
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
    throw e;
  }
  return data;
}

export const api = {
  system: () => request('GET', '/api/system'),
  version: () => request('GET', '/api/version'),
  releases: () => request('GET', '/api/releases'),
  migration: (from) => request('GET', `/api/releases/migration?from=${encodeURIComponent(from)}`),
  probe: (url, playlist = null) => request('POST', '/api/probe', { url, playlist }),
  createJob: (req) => request('POST', '/api/jobs', req),
  job: (id) => request('GET', `/api/jobs/${encodeURIComponent(id)}`),
  log: (id) => request('GET', `/api/jobs/${encodeURIComponent(id)}/log`),
  cancel: (id) => request('POST', `/api/jobs/${encodeURIComponent(id)}/cancel`),
  remove: (id) => request('DELETE', `/api/jobs/${encodeURIComponent(id)}`),
  eventsUrl: (id) => `/api/jobs/${encodeURIComponent(id)}/events`,
};
