// Angaza Care service worker.
// Static files: cache first. Pages: network first, falling back to the last copy
// seen, so a CHW can still read her cases and a mother's history without signal.
const VERSION = "angaza-v1";
const STATIC = ["/css/site.css", "/js/site.js", "/fonts/atkinson-hyperlegible-latin-400-normal.woff2",
  "/fonts/atkinson-hyperlegible-latin-700-normal.woff2", "/img/icon.svg", "/manifest.webmanifest", "/home/offline"];

self.addEventListener("install", (e) => {
  e.waitUntil(caches.open(VERSION).then((c) => c.addAll(STATIC)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (e) => {
  e.waitUntil(caches.keys().then((keys) => Promise.all(keys.filter((k) => k !== VERSION).map((k) => caches.delete(k))))
    .then(() => self.clients.claim()));
});

self.addEventListener("fetch", (e) => {
  const req = e.request;
  const url = new URL(req.url);
  if (req.method !== "GET" || url.origin !== location.origin) return;
  if (url.pathname.startsWith("/webhooks") || url.pathname.startsWith("/account") || url.pathname.startsWith("/simulator")) return;

  if (STATIC.includes(url.pathname) || url.pathname.startsWith("/fonts/") || url.pathname.startsWith("/img/")) {
    e.respondWith(caches.match(req).then((hit) => hit || fetch(req)));
    return;
  }

  if (req.mode === "navigate") {
    e.respondWith(
      fetch(req)
        .then((res) => {
          if (res.ok && !res.redirected) {
            const copy = res.clone();
            caches.open(VERSION).then((c) => c.put(req, copy));
          }
          return res;
        })
        .catch(() => caches.match(req).then((hit) => hit || caches.match("/home/offline")))
    );
  }
});
