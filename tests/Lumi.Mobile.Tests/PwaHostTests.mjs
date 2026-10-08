import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import vm from 'node:vm';

const source = name => readFileSync(
    new URL(`../../src/Lumi.Mobile.Browser/wwwroot/${name}`, import.meta.url), 'utf8');

function eventTarget(properties = {}) {
    const listeners = new Map();
    return Object.assign(properties, {
        addEventListener(name, callback) {
            const callbacks = listeners.get(name) ?? [];
            callbacks.push(callback);
            listeners.set(name, callbacks);
        },
        removeEventListener(name, callback) {
            listeners.set(name, (listeners.get(name) ?? []).filter(value => value !== callback));
        },
        dispatch(name, event = {}) {
            for (const callback of listeners.get(name) ?? [])
                callback(event);
        }
    });
}

function host({
    hidden = false, secure = true, dark = false, registrationError,
    origin = 'https://lumi.test', storage = new Map(), storageError, registration,
    controller = null, updateError, updateTask
} = {}) {
    const meta = {};
    const media = eventTarget({ matches: dark });
    const document = eventTarget({
        hidden,
        documentElement: { style: {}, dataset: {} },
        querySelector: () => meta
    });
    const registrations = [];
    const warnings = [];
    const navigations = [];
    const timers = new Map();
    let timerId = 0;
    let updates = 0;
    let reloads = 0;
    registration ??= eventTarget({
        waiting: null,
        installing: null,
        async update() {
            updates++;
            if (updateError)
                throw updateError;
            await updateTask?.();
            return this;
        }
    });
    const window = eventTarget({
        isSecureContext: secure,
        matchMedia: () => media,
        location: {
            origin,
            href: `${origin}/app/`,
            assign: url => navigations.push(url),
            reload: () => reloads++
        },
        sessionStorage: {
            getItem(key) {
                if (storageError)
                    throw storageError;
                return storage.get(key) ?? null;
            },
            setItem(key, value) {
                if (storageError)
                    throw storageError;
                storage.set(key, value);
            },
            removeItem(key) {
                if (storageError)
                    throw storageError;
                storage.delete(key);
            }
        },
        history: {
            state: null,
            replaceState(_state, _title, url) { window.location.href = url; }
        },
        launchQueue: { setConsumer(callback) { this.consume = callback; } }
    });
    const serviceWorker = eventTarget({
        controller,
        async register(url, options) {
            registrations.push({ url, options });
            if (registrationError)
                throw registrationError;
            return registration;
        }
    });
    const context = vm.createContext({
        document, window, URL, queueMicrotask,
        setTimeout(callback, ms) {
            const id = ++timerId;
            timers.set(id, { callback, ms });
            return id;
        },
        clearTimeout: id => timers.delete(id),
        navigator: { serviceWorker },
        console: { warn: (...args) => warnings.push(args) }
    });
    vm.runInContext(source('pwaHost.js').replace(/^export /gm, ''), context);
    return {
        context, document, window, media, meta, registrations, warnings, navigations, storage,
        registration, serviceWorker, timers,
        get updates() { return updates; },
        get reloads() { return reloads; }
    };
}

const settle = async () => {
    for (let i = 0; i < 8; i++)
        await Promise.resolve();
};

function waitingWorker() {
    const messages = [];
    return eventTarget({
        state: 'installed',
        messages,
        postMessage: message => messages.push(message.type)
    });
}

test('startup and duplicate resume events do not initiate extra handshakes', () => {
    const app = host();
    const active = [];
    app.context.configureAppLifecycle(value => active.push(value));
    app.window.dispatch('pageshow');
    app.window.launchQueue.consume();
    assert.deepEqual(active, []);

    app.document.hidden = true;
    app.document.dispatch('visibilitychange');
    app.window.dispatch('pagehide');
    app.window.dispatch('pageshow');
    assert.deepEqual(active, [false]);

    app.document.hidden = false;
    app.document.dispatch('visibilitychange');
    app.window.dispatch('pageshow');
    app.window.launchQueue.consume();
    assert.deepEqual(active, [false, true]);
});

test('a hidden startup and BFCache pagehide/pageshow publish the actual active state', () => {
    const app = host({ hidden: true });
    const active = [];
    app.context.configureAppLifecycle(value => active.push(value));
    assert.deepEqual(active, [false]);
    app.document.hidden = false;
    app.window.dispatch('pageshow');
    app.window.dispatch('pagehide');
    app.window.dispatch('pageshow');
    assert.deepEqual(active, [false, true, false, true]);
});

test('browser theme follows explicit Lumi choices and follows the OS only in System mode', () => {
    const app = host({ dark: true });
    assert.equal(app.meta.content, '#111114');
    app.context.setTheme('Light');
    assert.equal(app.meta.content, '#f7f7fa');
    app.media.dispatch('change');
    assert.equal(app.meta.content, '#f7f7fa');
    app.context.setTheme('Dark');
    app.media.matches = false;
    app.media.dispatch('change');
    assert.equal(app.meta.content, '#111114');
    app.context.setTheme('System');
    assert.equal(app.meta.content, '#f7f7fa');
    app.media.matches = true;
    app.media.dispatch('change');
    assert.equal(app.meta.content, '#111114');
    assert.equal(app.document.documentElement.style.colorScheme, 'dark');
});

test('explicit recovery reaches the same origin and removes its cache-bypass marker after startup', () => {
    const app = host();
    app.context.reloadWebApp();
    assert.deepEqual(app.navigations, ['https://lumi.test/app/?lumi-refresh=1']);
    app.window.location.href = 'https://lumi.test/app/?lumi-refresh=1&other=kept';
    app.context.configureAppLifecycle(() => {});
    assert.equal(app.window.location.href, 'https://lumi.test/app/?other=kept');
});

test('automatic gateway recovery crosses the network once until Lumi confirms a live connection', () => {
    const origin = 'https://private-47654.uks1.devtunnels.ms';
    const app = host({ origin });
    assert.equal(app.context.recoverGatewaySignIn(), true);
    assert.deepEqual(app.navigations, [`${origin}/app/?lumi-refresh=1`]);
    assert.equal(app.context.recoverGatewaySignIn(), false);
    const reopened = host({ origin, storage: app.storage });
    assert.equal(reopened.context.recoverGatewaySignIn(), false);
    reopened.context.confirmGatewaySignIn();
    assert.equal(reopened.context.recoverGatewaySignIn(), true);
    assert.deepEqual(reopened.navigations, [`${origin}/app/?lumi-refresh=1`]);
});

test('automatic Microsoft sign-in recovery is restricted to an HTTPS Dev Tunnel origin', () => {
    for (const origin of [
        'https://lumi.test', 'http://private-47654.uks1.devtunnels.ms',
        'https://private-47654.uks1.devtunnels.ms.attacker.test', 'https://my-pc.example.ts.net'
    ]) {
        const app = host({ origin });
        assert.equal(app.context.recoverGatewaySignIn(), false, origin);
        assert.equal(app.navigations.length, 0);
    }
});

test('manual recovery remains available after an unsuccessful automatic attempt', () => {
    const app = host({ origin: 'https://private-47654.uks1.devtunnels.ms' });
    assert.equal(app.context.recoverGatewaySignIn(), true);
    assert.equal(app.context.recoverGatewaySignIn(), false);
    app.context.reloadWebApp();
    assert.equal(app.navigations.length, 2);
    assert.equal(app.navigations[0], app.navigations[1]);
});

test('storage failure leaves explicit sign-in recovery available instead of entering a reload loop', () => {
    const app = host({
        origin: 'https://private-47654.uks1.devtunnels.ms',
        storageError: new Error('Storage is unavailable')
    });
    assert.equal(app.context.recoverGatewaySignIn(), false);
    assert.equal(app.navigations.length, 0);
    assert.match(app.warnings[0][0], /Automatic Microsoft sign-in recovery is unavailable/);
    app.context.reloadWebApp();
    assert.equal(app.navigations.length, 1);
});

test('registration is app-scoped, bypasses HTTP caches for updates and does not block boot on failure', async () => {
    const app = host();
    await app.context.registerAppCache();
    assert.equal(app.registrations.length, 1);
    assert.equal(app.registrations[0].url, './service-worker.js');
    assert.equal(app.registrations[0].options.scope, './');
    assert.equal(app.registrations[0].options.updateViaCache, 'none');
    const failed = host({ registrationError: new Error('Sign-in required') });
    await failed.context.registerAppCache();
    assert.equal(failed.warnings.length, 1);
    assert.match(failed.warnings[0][0], /launches will need the network/);
    const insecure = host({ secure: false });
    await insecure.context.registerAppCache();
    assert.equal(insecure.registrations.length, 0);
});

test('a waiting verified update defers until managed work is safe, then activates and reloads only once', async () => {
    const app = host({ controller: {} });
    const worker = waitingWorker();
    app.registration.waiting = worker;
    await app.context.registerAppCache();
    await settle();
    assert.equal(worker.messages.length, 0, 'WASM safety must be available before activation');
    let safe = false;
    const states = [];
    app.context.configureAppUpdates(() => safe, state => states.push(state));
    await settle();
    assert.deepEqual(states, ['ready']);
    assert.equal(worker.messages.length, 0);
    safe = true;
    app.context.tryApplyAppUpdate();
    app.context.tryApplyAppUpdate();
    await settle();
    assert.deepEqual(worker.messages, ['lumi-activate-update']);
    assert.equal(app.reloads, 0, 'Do not reload before the fresh controller owns this page');
    app.serviceWorker.controller = {};
    app.serviceWorker.dispatch('controllerchange');
    await settle();
    app.serviceWorker.dispatch('controllerchange');
    app.context.tryApplyAppUpdate();
    await settle();
    assert.equal(app.reloads, 1);
    assert.equal(app.navigations.length, 0, 'Normal updates do not use the sign-in/recovery link');
    assert.equal(app.timers.size, 0);
    assert.deepEqual(states, ['ready', 'applying', 'ready', 'applying']);
});

test('safe notifications are coalesced past transient draft restoration or send startup', async () => {
    const app = host({ controller: {} });
    const worker = waitingWorker();
    app.registration.waiting = worker;
    await app.context.registerAppCache();
    let safe = true;
    app.context.configureAppUpdates(() => safe, () => {});
    safe = false;
    await settle();
    assert.equal(worker.messages.length, 0);
    assert.equal(app.reloads, 0);
});

test('work started during activation still prevents reload until it is finished', async () => {
    const app = host({ controller: {} });
    const worker = waitingWorker();
    app.registration.waiting = worker;
    await app.context.registerAppCache();
    let safe = true;
    const states = [];
    app.context.configureAppUpdates(() => safe, state => states.push(state));
    await settle();
    safe = false;
    app.serviceWorker.controller = {};
    app.serviceWorker.dispatch('controllerchange');
    await settle();
    assert.equal(app.reloads, 0);
    assert.equal(states.at(-1), 'ready');
    safe = true;
    app.context.tryApplyAppUpdate();
    await settle();
    assert.equal(app.reloads, 1);
    assert.equal(worker.messages.length, 1);
});

test('another app window can activate an update without navigating this window or losing its draft', async () => {
    const app = host({ controller: {} });
    await app.context.registerAppCache();
    let draft = 'Keep this unsent text';
    const states = [];
    app.context.configureAppUpdates(() => draft.length === 0, state => states.push(state));
    app.serviceWorker.controller = {};
    app.serviceWorker.dispatch('controllerchange');
    await settle();
    assert.equal(draft, 'Keep this unsent text');
    assert.equal(app.reloads, 0);
    assert.equal(states.at(-1), 'ready');
    draft = '';
    app.context.tryApplyAppUpdate();
    await settle();
    assert.equal(app.reloads, 1);
});

test('first-time cache installation claims the app without an unnecessary reload', async () => {
    const app = host();
    await app.context.registerAppCache();
    app.context.configureAppUpdates(() => true, () => {});
    app.serviceWorker.controller = {};
    app.serviceWorker.dispatch('controllerchange');
    await settle();
    assert.equal(app.reloads, 0);
    const worker = waitingWorker();
    app.registration.waiting = worker;
    await app.context.checkAppUpdate();
    await settle();
    assert.deepEqual(worker.messages, ['lumi-activate-update']);
    app.serviceWorker.controller = {};
    app.serviceWorker.dispatch('controllerchange');
    await settle();
    assert.equal(app.reloads, 1);
});

test('unchanged assets only get checked on launch, resume, network recovery and desktop reconnect', async () => {
    const app = host({ controller: {} });
    const states = [];
    app.context.configureAppUpdates(() => true, state => states.push(state));
    app.context.configureAppLifecycle(() => {});
    await app.context.checkAppUpdate();
    assert.equal(app.updates, 1);
    assert.equal(app.registrations.length, 1);
    app.document.hidden = true;
    app.document.dispatch('visibilitychange');
    app.document.hidden = false;
    app.document.dispatch('visibilitychange');
    await app.context.checkAppUpdate();
    assert.equal(app.updates, 2);
    app.window.dispatch('online');
    await app.context.checkAppUpdate();
    assert.equal(app.updates, 3);
    await app.context.checkAppUpdate();
    assert.equal(app.updates, 4, 'A real desktop reconnect checks even just after an earlier launch');
    assert.equal(app.reloads, 0);
    assert.equal(app.navigations.length, 0);
    assert.deepEqual(states, ['current']);
});

test('concurrent launch, resume and reconnect checks share one in-flight update', async () => {
    let complete;
    const pending = new Promise(resolve => { complete = resolve; });
    const app = host({ controller: {}, updateTask: () => pending });
    const checks = [
        app.context.checkAppUpdate(),
        app.context.checkAppUpdate(),
        app.context.checkAppUpdate()
    ];
    await settle();
    assert.equal(app.updates, 1);
    complete();
    await Promise.all(checks);
    assert.equal(app.registrations.length, 1);
    assert.equal(app.updates, 1);
});

test('background pages do not apply waiting updates until a real resume', async () => {
    const app = host({ controller: {}, hidden: true });
    const worker = waitingWorker();
    app.registration.waiting = worker;
    await app.context.registerAppCache();
    app.context.configureAppLifecycle(() => {});
    app.context.configureAppUpdates(() => true, () => {});
    await settle();
    assert.equal(worker.messages.length, 0);
    app.document.hidden = false;
    app.window.dispatch('pageshow');
    await settle();
    assert.equal(worker.messages.length, 1);
});

test('failed downloads and update checks keep the running app and expose a retryable state', async () => {
    const app = host({ controller: {} });
    const states = [];
    app.context.configureAppUpdates(() => true, state => states.push(state));
    await app.context.registerAppCache();
    const worker = waitingWorker();
    worker.state = 'installing';
    app.registration.installing = worker;
    app.registration.dispatch('updatefound');
    assert.equal(states.at(-1), 'downloading');
    worker.state = 'redundant';
    app.registration.installing = null;
    worker.dispatch('statechange');
    assert.equal(states.at(-1), 'failed');
    assert.equal(app.reloads, 0);
    await app.context.checkAppUpdate();
    assert.equal(states.at(-1), 'current');

    const offline = host({ controller: {}, updateError: new Error('Offline') });
    const offlineStates = [];
    offline.context.configureAppUpdates(() => true, state => offlineStates.push(state));
    await offline.context.checkAppUpdate();
    assert.equal(offlineStates.at(-1), 'failed');
    assert.equal(offline.reloads, 0);
    assert.equal(offline.warnings.length, 1);
});

test('a failed initial registration can be retried without clearing pairing or other storage', async () => {
    const storage = new Map([['pairing-test', 'kept']]);
    const app = host({ storage, registrationError: new Error('Offline') });
    await app.context.checkAppUpdate();
    app.serviceWorker.register = async () => app.registration;
    await app.context.checkAppUpdate();
    assert.equal(app.updates, 1);
    assert.equal(storage.get('pairing-test'), 'kept');
    assert.equal(app.reloads, 0);
});

test('activation timeout or post failure is visible and does not reload an unactivated worker', async () => {
    for (const throwOnPost of [false, true]) {
        const app = host({ controller: {} });
        const worker = waitingWorker();
        if (throwOnPost)
            worker.postMessage = () => { throw new Error('Worker stopped'); };
        app.registration.waiting = worker;
        await app.context.registerAppCache();
        const states = [];
        app.context.configureAppUpdates(() => true, state => states.push(state));
        await settle();
        if (!throwOnPost) {
            const timer = [...app.timers.values()][0];
            assert.equal(timer.ms, 60000);
            timer.callback();
        }
        assert.equal(states.at(-1), 'failed');
        assert.equal(app.reloads, 0);
        assert.equal(app.warnings.length, 1);
    }
});

async function recovery({ scope = 'https://lumi.test/app/', failure, serviceWorker = true } = {}) {
    const status = { textContent: '' };
    const retry = eventTarget({ hidden: true });
    const navigations = [];
    const errors = [];
    const messages = [];
    let unregisters = 0;
    let updates = 0;
    const worker = eventTarget({
        state: 'installed',
        postMessage(value) {
            messages.push(value.type);
            this.state = 'activated';
            this.dispatch('statechange');
        }
    });
    const context = {
        document: { getElementById: id => id === 'cache-recovery-status' ? status : retry },
        window: {
            location: { origin: 'https://lumi.test', replace: url => navigations.push(url) },
            get localStorage() { throw new Error('Pairing storage must not be touched'); },
            get sessionStorage() { throw new Error('App session state must not be touched'); }
        },
        navigator: serviceWorker ? { serviceWorker: {
            async getRegistration(url) {
                assert.equal(url, 'https://lumi.test/app/');
                return { scope, async unregister() {
                    unregisters++;
                    if (failure)
                        throw failure;
                    return true;
                } };
            },
            async register(url, options) {
                assert.equal(url, 'https://lumi.test/app/service-worker.js');
                assert.equal(options.scope, 'https://lumi.test/app/');
                assert.equal(options.updateViaCache, 'none');
                return { waiting: worker, async update() { updates++; } };
            }
        } } : {},
        URL, setTimeout, clearTimeout,
        console: { error: (...args) => errors.push(args) }
    };
    await vm.runInNewContext(source('cache-recovery.js')
        .replace('void recoverCachedApp();', 'recoverCachedApp();'), context);
    return { unregisters, navigations, errors, retry, status, messages, updates };
}

test('fresh recovery leaves pairing and browser state alone while replacing only the app registration', async () => {
    const app = await recovery();
    assert.equal(app.unregisters, 1);
    assert.deepEqual(app.navigations, ['https://lumi.test/app/']);
    assert.deepEqual(app.messages, ['lumi-activate-recovery']);
    assert.equal(app.updates, 1);
    assert.equal(app.errors.length, 0);
    assert.equal(app.retry.hidden, true);
    const unrelated = await recovery({ scope: 'https://lumi.test/other/' });
    assert.equal(unrelated.unregisters, 0);
    assert.deepEqual(unrelated.navigations, ['https://lumi.test/app/']);
    const unsupported = await recovery({ serviceWorker: false });
    assert.deepEqual(unsupported.navigations, ['https://lumi.test/app/']);
});

test('fresh recovery exposes an unregister failure instead of looping or silently clearing state', async () => {
    const app = await recovery({ failure: new Error('Controller could not be removed') });
    assert.equal(app.unregisters, 1);
    assert.equal(app.navigations.length, 0);
    assert.equal(app.errors.length, 1);
    assert.equal(app.retry.hidden, false);
    assert.match(app.status.textContent, /pairing is kept/);
});

function worker({
    installError, missingCache = false,
    scope = 'https://lumi.test/app/', networkError, networkStatus = 200, networkType = 'basic',
    appClients = [], legacyFramework
} = {}) {
    const handlers = new Map();
    const requests = [];
    const network = [];
    const deleted = [];
    const networkOptions = [];
    const warnings = [];
    let claims = 0;
    let skips = 0;
    const cache = {
        async addAll(values) {
            requests.push(...values);
            if (installError)
                throw installError;
        },
        async match(url) {
            return missingCache ? undefined : { cached: url };
        }
    };
    const self = {
        registration: { scope },
        lumiAssetVersion: 'build2',
        lumiAssets: [
            { url: 'index.html', hash: 'sha256-shell' },
            { url: 'main.js', hash: 'sha256-main' },
            { url: '_framework/runtime.wasm', hash: 'sha256-wasm' }
        ],
        addEventListener: (name, callback) => handlers.set(name, callback),
        clients: { async claim() { claims++; }, async matchAll() { return appClients; } },
        skipWaiting() { skips++; }
    };
    vm.runInNewContext(source('service-worker.js'), {
        self, URL, Request, Set,
        importScripts: url => assert.equal(url, './service-worker-assets.js'),
        caches: {
            async open(name) {
                if (name === 'lumi-pwa-build1')
                    return { async match(url) { return url === legacyFramework ? { legacy: url } : undefined; } };
                assert.equal(name, 'lumi-pwa-build2');
                return cache;
            },
            async keys() { return ['lumi-pwa-build1', 'lumi-pwa-build2', 'unrelated-cache']; },
            async delete(name) { deleted.push(name); }
        },
        async fetch(request, options) {
            network.push(request);
            networkOptions.push(options);
            if (networkError)
                throw networkError;
            return { network: request.url, status: networkStatus, type: networkType };
        },
        console: { warn: (...args) => warnings.push(args) }
    });
    return {
        requests, network, deleted, networkOptions, warnings,
        get claims() { return claims; },
        get skips() { return skips; },
        async lifecycle(name) {
            let pending;
            handlers.get(name)({ waitUntil(value) { pending = value; } });
            await pending;
        },
        async message(type, url) {
            let pending;
            handlers.get('message')({
                data: { type }, source: { url },
                waitUntil(value) { pending = value; }
            });
            await pending;
        },
        async fetch(url, { method = 'GET', mode = 'cors' } = {}) {
            let pending;
            handlers.get('fetch')({
                request: { url, method, mode },
                respondWith(value) { pending = value; }
            });
            return pending;
        }
    };
}

test('install integrity-checks credentialed static assets and never forcibly activates an update', async () => {
    const app = worker();
    await app.lifecycle('install');
    assert.equal(app.requests.length, 3);
    for (const request of app.requests) {
        assert.equal(request.credentials, 'same-origin');
        assert.equal(request.mode, 'same-origin');
        assert.equal(request.redirect, 'error');
        assert.equal(request.cache, 'reload');
        assert.match(request.integrity, /^sha256-/);
        assert.ok(request.url.startsWith('https://lumi.test/app/'));
    }
    assert.equal(app.skips, 0);
    assert.equal(app.claims, 0);
});

test('a rejected asset prevents installation instead of accepting a partial app cache', async () => {
    const app = worker({ installError: new Error('Integrity mismatch') });
    await assert.rejects(app.lifecycle('install'), /Integrity mismatch/);
    assert.equal(app.claims, 0);
    assert.equal(app.skips, 0);
});

test('activation removes only obsolete Lumi app caches', async () => {
    const app = worker();
    await app.lifecycle('activate');
    assert.deepEqual(app.deleted, ['lumi-pwa-build1']);
    assert.equal(app.claims, 1);
});

test('coordinated recovery activation retains older verified runtimes for other open app pages', async () => {
    const app = worker({
        appClients: [{ url: 'https://lumi.test/app/?lumi-fresh=1' }, { url: 'https://lumi.test/app/' }],
        legacyFramework: 'https://lumi.test/app/_framework/runtime.oldhash.wasm'
    });
    await app.message('lumi-activate-recovery', 'https://elsewhere.test/app/');
    assert.equal(app.skips, 0);
    await app.message('lumi-activate-recovery', 'https://lumi.test/outside/');
    assert.equal(app.skips, 0);
    await app.message('lumi-activate-recovery', 'https://lumi.test/app/');
    assert.equal(app.skips, 1);
    await app.lifecycle('activate');
    assert.equal(app.deleted.length, 0);
    assert.deepEqual(await app.fetch('https://lumi.test/app/_framework/runtime.oldhash.wasm'),
        { legacy: 'https://lumi.test/app/_framework/runtime.oldhash.wasm' });
    assert.equal(await app.fetch('https://lumi.test/lumi/transcript'), undefined);
    assert.equal(app.network.length, 0);
});

test('normal update activation has the same app-scoped trust boundary and never navigates other clients', async () => {
    const app = worker({
        appClients: [{ url: 'https://lumi.test/app/' }, { url: 'https://lumi.test/app/?other=1' }],
        legacyFramework: 'https://lumi.test/app/_framework/runtime.oldhash.wasm'
    });
    await app.message('lumi-activate-update', 'https://elsewhere.test/app/');
    await app.message('lumi-activate-update', 'https://lumi.test/outside/');
    await app.message('unknown', 'https://lumi.test/app/');
    assert.equal(app.skips, 0);
    await app.message('lumi-activate-update', 'https://lumi.test/app/');
    assert.equal(app.skips, 1);
    await app.lifecycle('activate');
    assert.equal(app.deleted.length, 0);
    assert.deepEqual(await app.fetch('https://lumi.test/app/_framework/runtime.oldhash.wasm'),
        { legacy: 'https://lumi.test/app/_framework/runtime.oldhash.wasm' });
});

test('warm icon navigation and WASM resources use the installed cache without network requests', async () => {
    const app = worker();
    assert.deepEqual(await app.fetch('https://lumi.test/app/', { mode: 'navigate' }),
        { cached: 'https://lumi.test/app/index.html' });
    assert.deepEqual(await app.fetch('https://lumi.test/app/index.html?source=icon', { mode: 'navigate' }),
        { cached: 'https://lumi.test/app/index.html' });
    assert.deepEqual(await app.fetch('https://lumi.test/app/_framework/runtime.wasm'),
        { cached: 'https://lumi.test/app/_framework/runtime.wasm' });
    assert.equal(app.network.length, 0);
});

test('every private Dev Tunnel launch checks document sign-in even when the shell is cached', async () => {
    const origin = 'https://private-47654.uks1.devtunnels.ms';
    const app = worker({ scope: origin + '/app/' });
    for (const launch of ['/app/', '/app/index.html?source=icon', '/app/']) {
        const result = await app.fetch(origin + launch, { mode: 'navigate' });
        assert.equal(result.network, origin + launch);
    }
    assert.equal(app.network.length, 3);
    assert.ok(app.networkOptions.every(options => options.cache === 'no-store'));
    assert.equal(app.requests.length, 0);
});

test('a private launch never replaces a Microsoft sign-in response with cached application HTML', async () => {
    const origin = 'https://private-47654.uks1.devtunnels.ms';
    for (const [status, type] of [[401, 'basic'], [403, 'basic'], [0, 'opaqueredirect']]) {
        const app = worker({ scope: origin + '/app/', networkStatus: status, networkType: type });
        const result = await app.fetch(origin + '/app/', { mode: 'navigate' });
        assert.equal(result.status, status);
        assert.equal(result.type, type);
        assert.equal(result.cached, undefined);
        assert.equal(app.warnings.length, 0);
    }
});

test('private tunnel launches retain an explicit cached-shell fallback when offline or unavailable', async () => {
    const origin = 'https://private-47654.uks1.devtunnels.ms';
    for (const options of [
        { networkError: new Error('Offline') }, { networkStatus: 502 },
        { networkStatus: 503 }, { networkStatus: 504 }
    ]) {
        const app = worker({ scope: origin + '/app/', ...options });
        assert.deepEqual(await app.fetch(origin + '/app/', { mode: 'navigate' }),
            { cached: origin + '/app/index.html' });
        assert.equal(app.warnings.length, 1);
        assert.equal(app.requests.length, 0);
    }
    const missing = worker({
        scope: origin + '/app/', missingCache: true, networkError: new Error('Offline')
    });
    await assert.rejects(missing.fetch(origin + '/app/', { mode: 'navigate' }), /Offline/);
});

test('private navigation sign-in does not put WASM or live APIs on the document network path', async () => {
    const origin = 'https://private-47654.uks1.devtunnels.ms';
    const app = worker({ scope: origin + '/app/' });
    assert.deepEqual(await app.fetch(origin + '/app/_framework/runtime.wasm'),
        { cached: origin + '/app/_framework/runtime.wasm' });
    assert.equal(await app.fetch(origin + '/lumi/hello'), undefined);
    assert.equal(await app.fetch(origin + '/lumi/events'), undefined);
    assert.equal(app.network.length, 0);
});

test('fresh recovery documents and scripts always escape the static app cache', async () => {
    const app = worker();
    assert.equal(await app.fetch('https://lumi.test/app/cache-recovery.html', { mode: 'navigate' }), undefined);
    assert.equal(await app.fetch('https://lumi.test/app/cache-recovery.js'), undefined);
    assert.equal(app.network.length, 0);
    assert.equal(app.requests.length, 0);
});

test('APIs, files, foreign origins, commands and unknown assets are never intercepted or cached', async () => {
    const app = worker();
    for (const url of [
        'https://lumi.test/lumi/hello', 'https://lumi.test/lumi/pair',
        'https://lumi.test/lumi/events', 'https://lumi.test/lumi/transcript',
        'https://lumi.test/lumi/files/secret.txt', 'https://other.test/app/main.js',
        'https://lumi.test/app/unknown.js'
    ]) {
        assert.equal(await app.fetch(url), undefined, url);
    }
    assert.equal(await app.fetch('https://lumi.test/app/main.js', { method: 'POST' }), undefined);
    assert.equal(await app.fetch('https://lumi.test/app/?lumi-refresh=1', { mode: 'navigate' }), undefined);
    assert.equal(app.network.length, 0);
});

test('an evicted cache falls back to the real request without storing an unchecked response', async () => {
    const app = worker({ missingCache: true });
    assert.deepEqual(await app.fetch('https://lumi.test/app/main.js'),
        { network: 'https://lumi.test/app/main.js', status: 200, type: 'basic' });
    assert.equal(app.network.length, 1);
    assert.equal(app.requests.length, 0);
});

const publishRoot = process.env.LUMI_PWA_PUBLISH_ROOT;
test('published inventory covers final app/WASM bytes with stable content hashes', { skip: !publishRoot }, () => {
    const content = readFileSync(path.join(publishRoot, 'service-worker-assets.js'), 'utf8');
    const self = {};
    vm.runInNewContext(content, { self });
    const assets = self.lumiAssets;
    assert.ok(assets.length > 5);
    for (const name of ['index.html', 'main.js', 'browserHost.js', 'pwaHost.js',
        'manifest.webmanifest', 'service-worker.js', '_framework/dotnet.js']) {
        assert.ok(assets.some(asset => asset.url === name), `Missing ${name}`);
    }
    assert.ok(!assets.some(asset => asset.url.startsWith('cache-recovery.')));
    assert.ok(assets.some(asset => asset.url.endsWith('.wasm')));
    assert.equal(new Set(assets.map(asset => asset.url)).size, assets.length);
    for (const asset of assets) {
        assert.ok(!asset.url.includes('\\') && !asset.url.includes('..'));
        assert.ok(!/\.(br|gz|map)$/.test(asset.url));
        assert.notEqual(asset.url, 'service-worker-assets.js');
        const bytes = readFileSync(path.join(publishRoot, ...asset.url.split('/')));
        assert.equal(asset.hash, 'sha256-' + createHash('sha256').update(bytes).digest('base64'), asset.url);
    }
    const inventoryBytes = content.slice(0, content.indexOf('self.lumiAssetVersion'));
    assert.equal(self.lumiAssetVersion, createHash('sha256').update(inventoryBytes).digest('hex').toUpperCase());
});
