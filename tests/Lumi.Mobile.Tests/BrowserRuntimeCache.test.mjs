import assert from 'node:assert/strict';
import { beforeEach, test } from 'node:test';
import { loadRuntimeResource } from '../../src/Lumi.Mobile.Browser/wwwroot/runtimeResourceCache.js';

const origin = 'https://lumi.example';
const url = `${origin}/app/_framework/dotnet.native.abc12345.wasm`;
const integrity = 'sha256-runtime-build-one';
let entries;
let cache;
let fetchMock;
let warnings;

function load(hash = integrity, resourceUrl = url) {
    return loadRuntimeResource('dotnetwasm', 'dotnet.native.abc12345.wasm', resourceUrl, hash);
}

beforeEach(t => {
    entries = new Map();
    cache = {
        match: t.mock.fn(async key => entries.get(key)?.clone()),
        put: t.mock.fn(async (key, response) => entries.set(key, response)),
        keys: t.mock.fn(async () => [...entries.keys()].map(key => new Request(key))),
        delete: t.mock.fn(async request => entries.delete(request.url))
    };
    for (const [name, value] of Object.entries({
        location: { href: `${origin}/app/`, origin },
        caches: { open: t.mock.fn(async () => cache) }
    })) {
        const original = Object.getOwnPropertyDescriptor(globalThis, name);
        Object.defineProperty(globalThis, name, { configurable: true, value });
        t.after(() => {
            if (original)
                Object.defineProperty(globalThis, name, original);
            else
                delete globalThis[name];
        });
    }
    fetchMock = t.mock.method(globalThis, 'fetch', async () =>
        new Response('runtime', { headers: { 'Content-Type': 'application/wasm' } }));
    warnings = t.mock.method(console, 'warn', () => {});
});

test('reopening uses the saved native runtime without another network request', async () => {
    assert.equal(await (await load()).text(), 'runtime');
    assert.equal(await (await load()).text(), 'runtime');
    assert.equal(fetchMock.mock.callCount(), 1);
    assert.equal(cache.put.mock.callCount(), 1);
    assert.equal(globalThis.caches.open.mock.calls[0].arguments[0], 'lumi-wasm-runtime-v1');
    assert.equal(entries.size, 1);
});

test('downloads retain integrity, credentials and same-origin restrictions', async () => {
    await load();
    assert.deepEqual(fetchMock.mock.calls[0].arguments, [url, {
        cache: 'force-cache',
        credentials: 'same-origin',
        mode: 'same-origin',
        redirect: 'error',
        integrity
    }]);
    assert.equal(new URL([...entries.keys()][0]).searchParams.get('integrity'), integrity);
});

test('a changed build hash cannot reuse an old runtime and replaces only its cache entry', async () => {
    await load();
    await load('sha256-runtime-build-two');
    assert.equal(fetchMock.mock.callCount(), 2);
    assert.equal(entries.size, 1);
    assert.equal(new URL([...entries.keys()][0]).searchParams.get('integrity'), 'sha256-runtime-build-two');
});

test('an updated fingerprinted filename downloads the new runtime', async () => {
    await load();
    await load(integrity, `${origin}/app/_framework/dotnet.native.def67890.wasm`);
    assert.equal(fetchMock.mock.callCount(), 2);
    assert.equal(entries.size, 1);
    assert.match([...entries.keys()][0], /dotnet\.native\.def67890\.wasm/);
});

test('other boot resources and unhashed debug builds keep the default loader', () => {
    for (const type of ['assembly', 'dotnetjs', 'manifest', 'globalization'])
        assert.equal(loadRuntimeResource(type, 'resource', url, integrity), undefined);
    assert.equal(loadRuntimeResource('dotnetwasm', 'runtime', url, ''), undefined);
    assert.equal(fetchMock.mock.callCount(), 0);
    assert.equal(globalThis.caches.open.mock.callCount(), 0);
});

test('rejects foreign origins and API URLs before reading storage or sending a request', () => {
    for (const resourceUrl of ['https://other.example/app/_framework/runtime.wasm', `${origin}/lumi/snapshot`])
        assert.throws(() => load(integrity, resourceUrl), /must be loaded from this PC/);
    assert.equal(fetchMock.mock.callCount(), 0);
    assert.equal(globalThis.caches.open.mock.callCount(), 0);
});

test('unavailable Cache Storage falls back to an integrity-checked download', async () => {
    delete globalThis.caches;
    assert.equal(await (await load()).text(), 'runtime');
    assert.equal(fetchMock.mock.callCount(), 1);
    assert.equal(cache.put.mock.callCount(), 0);
});

test('denied storage is logged without blocking startup', async () => {
    globalThis.caches.open.mock.mockImplementation(async () => { throw new Error('Storage denied'); });
    assert.equal(await (await load()).text(), 'runtime');
    assert.equal(warnings.mock.callCount(), 1);
    assert.equal(fetchMock.mock.callCount(), 1);
});

test('a failed cache read falls back without masking the storage error', async () => {
    cache.match.mock.mockImplementation(async () => { throw new Error('Read failed'); });
    assert.equal(await (await load()).text(), 'runtime');
    assert.equal(warnings.mock.callCount(), 1);
    assert.equal(cache.put.mock.callCount(), 0);
});

test('a full cache does not discard the downloaded response or the previous build', async () => {
    await load();
    cache.put.mock.mockImplementation(async () => { throw new Error('Quota exceeded'); });
    assert.equal(await (await load('sha256-runtime-build-two')).text(), 'runtime');
    assert.equal(warnings.mock.callCount(), 1);
    assert.equal(entries.size, 1);
    assert.equal(new URL([...entries.keys()][0]).searchParams.get('integrity'), integrity);
});

test('failed network or integrity checks propagate and are never cached', async () => {
    fetchMock.mock.mockImplementation(async () => { throw new TypeError('Integrity check failed'); });
    await assert.rejects(load(), /Integrity check failed/);
    assert.equal(cache.put.mock.callCount(), 0);
    assert.equal(warnings.mock.callCount(), 0);
});

test('HTTP errors and sign-in HTML are not cached as the runtime', async () => {
    fetchMock.mock.mockImplementation(async () => new Response('Unavailable', { status: 503 }));
    await assert.rejects(load(), /HTTP 503/);
    fetchMock.mock.mockImplementation(async () =>
        new Response('<html>Sign in</html>', { headers: { 'Content-Type': 'text/html' } }));
    await assert.rejects(load(), /unexpected response/);
    assert.equal(cache.put.mock.callCount(), 0);
});
