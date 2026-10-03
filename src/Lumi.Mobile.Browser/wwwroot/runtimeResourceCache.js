const cacheName = 'lumi-wasm-runtime-v1';

export function loadRuntimeResource(type, _name, defaultUri, integrity) {
    if (type !== 'dotnetwasm' || !integrity)
        return undefined;

    const url = new URL(defaultUri, globalThis.location.href);
    if (url.origin !== globalThis.location.origin || !url.pathname.startsWith('/app/_framework/'))
        throw new Error('Lumi runtime must be loaded from this PC.');

    return loadRuntime(url, integrity);
}

async function loadRuntime(url, integrity) {
    const key = new URL(url);
    key.searchParams.set('integrity', integrity);
    let cache;
    try {
        if (globalThis.caches) {
            cache = await globalThis.caches.open(cacheName);
            const cached = await cache.match(key.href);
            if (cached)
                return cached;
        }
    } catch (error) {
        console.warn('[Lumi] Runtime cache unavailable; downloading instead.', error);
        cache = undefined;
    }

    const response = await fetch(url.href, {
        cache: 'force-cache',
        credentials: 'same-origin',
        mode: 'same-origin',
        redirect: 'error',
        integrity
    });
    if (!response.ok)
        throw new Error(`Lumi runtime download failed (HTTP ${response.status}).`);
    if (response.headers.get('Content-Type')?.split(';')[0].trim() !== 'application/wasm')
        throw new Error('Lumi received an unexpected response instead of its runtime.');

    // The native runtime can exceed the browser's per-resource HTTP cache limit.
    // Cache Storage retains it across PWA launches, keyed by the verified build hash.
    if (cache) {
        try {
            await cache.put(key.href, response.clone());
            const previous = await cache.keys();
            await Promise.all(previous
                .filter(request => request.url !== key.href)
                .map(request => cache.delete(request)));
        } catch (error) {
            console.warn('[Lumi] Could not cache the runtime for the next launch.', error);
        }
    }
    return response;
}
