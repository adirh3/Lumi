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

// ── Notifications ────────────────────────────────────────────────────────────────────────
// The PC pushes each notification through the browser's push service, encrypted for this browser.
// The page leaves the words they use, in the app's language, in their own cache.

const notificationTextUrl = new URL('notification-text.json', appUrl).href;
const englishNotificationText = {
    lang: 'en',
    testTitle: 'Notifications work',
    testText: 'This is how {0} will reach you while you\'re away.',
    inputText: 'Needs your input: {0}',
    approvalText: 'Needs your approval on {0}: {1}',
    replyFallback: 'Finished replying.'
};

const fill = (template, ...values) =>
    String(template).replace(/\{(\d+)\}/g, (match, index) => String(values[Number(index)] ?? match));

async function notificationText() {
    try {
        const saved = await (await caches.open('lumi-notifications')).match(notificationTextUrl);
        if (saved)
            return { ...englishNotificationText, ...await saved.json() };
    } catch (error) {
        console.warn('[Lumi PWA] The notification wording is unavailable; using English.', error);
    }
    return englishNotificationText;
}

async function showPush(data) {
    let message = {};
    try {
        message = data?.json() ?? {};
    } catch (error) {
        console.warn('[Lumi PWA] A notification could not be read.', error);
    }

    const text = await notificationText();
    const host = message.host ?? '';
    const isTest = message.kind === 'test';
    const words = isTest ? fill(text.testText, host)
        : message.kind === 'input' ? fill(text.inputText, message.text ?? '')
        : message.kind === 'approval' ? fill(text.approvalText, host, message.text ?? '')
        : message.kind === 'reply' ? message.text || text.replyFallback
        : message.text ?? '';
    const chatId = typeof message.chatId === 'string' ? message.chatId : null;
    const tag = chatId ?? (isTest ? 'lumi-test' : 'lumi');
    const at = typeof message.at === 'number' ? message.at : Date.now();

    // Push services keep no order, so a reply can arrive after a question that came later. What
    // the chat shows about something newer stays: it is shown again unchanged, because every push
    // must show a notification.
    const newer = (await self.registration.getNotifications({ tag }))
        .find(shown => typeof shown.data?.at === 'number' && shown.data.at > at);
    if (newer) {
        await self.registration.showNotification(newer.title, {
            body: newer.body, tag, renotify: false, timestamp: newer.data.at, lang: newer.lang,
            icon: newer.icon, badge: newer.badge, data: newer.data
        });
        return message.kind;
    }

    // A chat shows one notification: a newer one replaces it and rings again.
    await self.registration.showNotification(isTest ? text.testTitle : message.title || 'Lumi', {
        body: !isTest && message.agent ? `${message.agent}: ${words}` : words,
        tag,
        renotify: true,
        timestamp: at,
        lang: text.lang,
        icon: new URL('icons/lumi-192.png', appUrl).href,
        badge: new URL('icons/lumi-badge-96.png', appUrl).href,
        data: { chatId, title: typeof message.title === 'string' ? message.title : '', at }
    });
    return message.kind;
}

// The open app hears that a push arrived, so its test can tell a browser that delivers from one that
// accepted a subscription and never does.
async function reportPush(kind) {
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    for (const client of windows) {
        if (!client.url.startsWith(appUrl.href))
            continue;
        try {
            client.postMessage({ type: 'lumi-push-received', kind });
        } catch (error) {
            console.warn('[Lumi PWA] The app could not be told a notification arrived.', error);
        }
    }
}

self.addEventListener('push', event => {
    event.waitUntil(showPush(event.data).then(reportPush));
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    const { chatId, title } = event.notification.data ?? {};
    event.waitUntil((async () => {
        const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        const app = windows.find(client => client.url.startsWith(appUrl.href));
        if (app) {
            try {
                await app.focus();
            } catch (error) {
                console.warn('[Lumi PWA] The app could not be brought forward.', error);
            }
            if (chatId)
                app.postMessage({ type: 'lumi-open-chat', chatId, title });
            return;
        }

        const url = new URL('./', appUrl);
        if (chatId)
            url.searchParams.set('lumi-chat', chatId);
        await self.clients.openWindow(url.href);
    })());
});
