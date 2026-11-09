// ark-ydl service worker: makes the app installable and keeps the shell usable on a flaky
// connection. Network first, always — the API and downloads are never cached.
// Named after the release (ReleaseTests keeps it equal to <Version>), so each release re-caches the shell once.
const CACHE = 'ark-ydl-shell-1.1.0';
const SHELL = ['/', '/css/app.css', '/js/app.js', '/js/util.js', '/js/api.js', '/js/clip.js', '/js/jobs.js',
  '/js/release.js', '/js/version.js',
  '/img/mark.svg', '/img/icon.svg', '/img/favicon.svg', '/manifest.webmanifest'];

self.addEventListener('install', (e) => {
  e.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)).catch(() => {}).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (e) => {
  e.waitUntil(caches.keys()
    .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
    .then(() => self.clients.claim()));
});

self.addEventListener('fetch', (e) => {
  const req = e.request;
  const url = new URL(req.url);
  if (req.method !== 'GET' || url.origin !== location.origin) return;
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/admin') || url.pathname.startsWith('/auth/') || url.pathname.startsWith('/signin') || url.pathname.startsWith('/signout')) return;
  const isShell = req.mode === 'navigate' || SHELL.includes(url.pathname);
  if (!isShell) return;
  e.respondWith(
    fetch(req)
      .then((res) => {
        if (res.ok && res.type === 'basic') {
          const copy = res.clone();
          caches.open(CACHE).then((c) => c.put(req.mode === 'navigate' ? '/' : req, copy)).catch(() => {});
        }
        return res;
      })
      .catch(() => caches.match(req.mode === 'navigate' ? '/' : req).then((r) => r || Response.error())),
  );
});
