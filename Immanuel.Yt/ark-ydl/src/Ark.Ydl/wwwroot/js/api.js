// Thin wrapper over the ark-ydl HTTP API. Errors surface as Error(message) with .status / .detail.

async function request(method, url, body) {
  let res;
  try {
    res = await fetch(url, {
      method,
      credentials: 'same-origin',
      headers: body ? { 'Content-Type': 'application/json' } : undefined,
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
  probe: (url, playlist = null) => request('POST', '/api/probe', { url, playlist }),
  createJob: (req) => request('POST', '/api/jobs', req),
  job: (id) => request('GET', `/api/jobs/${encodeURIComponent(id)}`),
  log: (id) => request('GET', `/api/jobs/${encodeURIComponent(id)}/log`),
  cancel: (id) => request('POST', `/api/jobs/${encodeURIComponent(id)}/cancel`),
  remove: (id) => request('DELETE', `/api/jobs/${encodeURIComponent(id)}`),
  eventsUrl: (id) => `/api/jobs/${encodeURIComponent(id)}/events`,
};
