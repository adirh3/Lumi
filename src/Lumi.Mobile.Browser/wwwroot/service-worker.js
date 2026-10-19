importScripts('./service-worker-assets.js');

const cachePrefix = 'lumi-pwa-';
const cacheName = cachePrefix + self.lumiAssetVersion;
const appUrl = new URL(self.registration.scope);
const shellUrl = new URL('index.html', appUrl).href;
const assetUrls = new Set(self.lumiAssets.map(asset => new URL(asset.url, appUrl).href));

self.addEventListener('install', event => {
    // Integrity also rejects a tunnel sign-in page returned in place of an app asset.
    const requests = self.lumiAssets.map(asset => new Request(new URL(asset.url, appUrl), {
        integrity: asset.hash,
        credentials: 'same-origin',
        mode: 'same-origin',
        redirect: 'error',
        cache: 'reload'
    }));
    event.waitUntil((async () => {
        const cache = await caches.open(cacheName);
        // Safari can reject a multi-asset addAll even when each verified asset caches successfully.
        for (let index = 0; index < requests.length; index += 4) {
            await Promise.all(requests.slice(index, index + 4).map(async request => {
                try {
                    await cache.add(request);
                } catch (error) {
                    console.warn(`[Lumi PWA] Could not cache ${new URL(request.url).pathname}.`, error);
                    throw error;
                }
            }));
        }
    })());
    // Do not skipWaiting: a desktop update must not replace a running app's WASM or draft.
});

self.addEventListener('activate', event => {
    event.waitUntil((async () => {
        const appClients = (await self.clients.matchAll({ type: 'window', includeUncontrolled: true }))
            .filter(client => client.url.startsWith(appUrl.href));
        if (appClients.length <= 1) {
            for (const name of await caches.keys()) {
                if (name.startsWith(cachePrefix) && name !== cacheName)
                    await caches.delete(name);
            }
        }
        await self.clients.claim();
    })());
});

self.addEventListener('message', event => {
    if (!['lumi-activate-recovery', 'lumi-activate-update'].includes(event.data?.type)
        || !event.source?.url)
        return;
    const source = new URL(event.source.url);
    if (source.origin === appUrl.origin && source.pathname.startsWith(appUrl.pathname))
        event.waitUntil(self.skipWaiting());
});

self.addEventListener('fetch', event => {
    const request = event.request;
    const url = new URL(request.url);
    if (request.method !== 'GET' || url.origin !== appUrl.origin)
        return;

    if (url.pathname === new URL('cache-recovery.html', appUrl).pathname
        || url.pathname === new URL('cache-recovery.js', appUrl).pathname)
        return;

    const isAppNavigation = request.mode === 'navigate'
        && (url.pathname === appUrl.pathname || url.href.split('?')[0] === shellUrl);
    // Let the browser handle gateway redirects without rebuilding the cache for sign-in.
    if (isAppNavigation
        && (url.searchParams.has('lumi-refresh') || url.searchParams.has('lumi-signin')))
        return;

    if (isAppNavigation
        && appUrl.protocol === 'https:'
        && appUrl.hostname.endsWith('.devtunnels.ms')) {
        event.respondWith((async () => {
            let response;
            let networkError;
            try {
                response = await fetch(request, { cache: 'no-store' });
                if (![502, 503, 504].includes(response.status))
                    return response;
                networkError = new Error(`The private tunnel returned HTTP ${response.status}.`);
            } catch (error) {
                networkError = error;
            }

            const cache = await caches.open(cacheName);
            const shell = await cache.match(shellUrl);
            if (shell) {
                console.warn('[Lumi PWA] The private tunnel is unavailable; opening the cached shell. Live data still requires a connection.', networkError);
                return shell;
            }
            if (response)
                return response;
            throw networkError;
        })());
        return;
    }

    url.search = '';
    if (!isAppNavigation && !assetUrls.has(url.href)) {
        if (url.pathname.startsWith(new URL('_framework/', appUrl).pathname)) {
            event.respondWith((async () => {
                for (const name of await caches.keys()) {
                    if (!name.startsWith(cachePrefix))
                        continue;
                    const cached = await (await caches.open(name)).match(url.href);
                    if (cached)
                        return cached;
                }
                return fetch(request);
            })());
        }
        return;
    }

    event.respondWith((async () => {
        const cache = await caches.open(cacheName);
        return await cache.match(isAppNavigation ? shellUrl : url.href) ?? fetch(request);
    })());
});
