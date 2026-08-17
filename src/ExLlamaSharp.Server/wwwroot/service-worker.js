/* Do not cache HTML or Blazor circuit — that freezes the first-run Admin UI. */
const CACHE = 'exllamasharp-admin-v2';

self.addEventListener('install', (event) => {
  self.skipWaiting();
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys().then((keys) => Promise.all(keys.map((k) => caches.delete(k))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', (event) => {
  const req = event.request;
  if (req.method !== 'GET') return;

  const url = new URL(req.url);
  if (url.origin !== self.location.origin) return;
  if (req.mode === 'navigate') return;
  if (url.pathname === '/' || url.pathname.startsWith('/_blazor') || url.pathname.startsWith('/_framework')) return;

  const staticAsset = /\.(css|js|ico|png|woff2?|webmanifest)$/i.test(url.pathname);
  if (!staticAsset) return;

  event.respondWith(
    fetch(req).then((res) => {
      if (res.ok) {
        const copy = res.clone();
        caches.open(CACHE).then((cache) => cache.put(req, copy)).catch(() => {});
      }
      return res;
    }).catch(() => caches.match(req))
  );
});
