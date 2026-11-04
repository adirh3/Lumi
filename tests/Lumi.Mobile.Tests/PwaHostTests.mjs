import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import vm from 'node:vm';
import { brotliDecompressSync, gunzipSync } from 'node:zlib';

const source = name => readFileSync(
    new URL(`../../src/Lumi.Mobile.Browser/wwwroot/${name}`, import.meta.url), 'utf8');

function eventTarget(properties = {}) {
    const listeners = new Map();
    return Object.assign(properties, {
        addEventListener(name, callback) {
            const callbacks = listeners.get(name) ?? [];
            if (!callbacks.includes(callback))
                callbacks.push(callback);
            listeners.set(name, callbacks);
        },
        removeEventListener(name, callback) {
            listeners.set(name, (listeners.get(name) ?? []).filter(value => value !== callback));
        },
        dispatch(name, event = {}) {
            for (const callback of listeners.get(name) ?? [])
                callback(event);
        },
        dispatchEvent(event) {
            this.dispatch(event.type, event);
            return true;
        }
    });
}

function host({
    hidden = false, secure = true, dark = false, registrationError,
    origin = 'https://lumi.test', storage = new Map(), storageError, registration,
    controller = null, updateError, updateTask, notifications
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
        active: {},
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
    registration.scope ??= `${origin}/app/`;
    if (notifications) {
        registration.pushManager ??= notifications.pushManager ?? pushManager();
        registration.getNotifications ??= async ({ tag } = {}) =>
            (notifications.shown ?? []).filter(notification => tag === undefined || notification.tag === tag);
    }
    const window = eventTarget({
        isSecureContext: secure,
        matchMedia: query => query.includes('display-mode')
            ? { matches: notifications?.standalone === true }
            : media,
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
    if (notifications && notifications.supported !== false) {
        window.Notification = {
            permission: notifications.permission ?? 'default',
            async requestPermission() {
                this.permission = notifications.answer ?? 'granted';
                return this.permission;
            }
        };
        window.PushManager = function PushManager() {};
    }
    const serviceWorker = eventTarget({
        controller,
        async register(url, options) {
            registrations.push({ url, options });
            if (registrationError)
                throw registrationError;
            return registration;
        },
        ready: Promise.resolve(registration),
        async getRegistration() { return registration; },
        startMessages() { serviceWorker.messagesStarted = true; }
    });
    const context = vm.createContext({
        document, window, URL, queueMicrotask, Response, atob,
        setTimeout(callback, ms) {
            const id = ++timerId;
            timers.set(id, { callback, ms });
            return id;
        },
        clearTimeout: id => timers.delete(id),
        navigator: {
            serviceWorker,
            userAgent: notifications?.userAgent ?? 'Mozilla/5.0 (Linux; Android 15) Chrome/143.0.0.0 Mobile',
            platform: notifications?.platform ?? 'Linux armv8l',
            maxTouchPoints: notifications?.maxTouchPoints ?? 5
        },
        caches: notifications?.caches,
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

const notifiedChat = '8a0f6c4e-5d0a-4c55-9c93-1f0b2a7d4e61';
const pcKey = Buffer.from([4, ...Array.from({ length: 64 }, (_, index) => index)]).toString('base64url');
// Objects made inside a test's VM come from its own realm; compare their plain JSON.
const plain = value => JSON.parse(JSON.stringify(value));

/** A browser's push manager: the subscription it holds, and what was asked of it. */
function pushManager({ existing = null, endpoint = 'https://fcm.googleapis.com/fcm/send/browser' } = {}) {
    const calls = { subscribed: [], unsubscribed: 0 };
    const subscription = (key, url) => ({
        options: { applicationServerKey: key },
        toJSON: () => ({ endpoint: url, expirationTime: null, keys: { p256dh: 'BBrowserKey', auth: 'Secret' } }),
        async unsubscribe() {
            calls.unsubscribed++;
            manager.current = null;
            return true;
        }
    });
    const manager = {
        calls,
        current: existing ? subscription(existing.key, existing.endpoint ?? endpoint) : null,
        async getSubscription() { return manager.current; },
        async subscribe(options) {
            calls.subscribed.push(options);
            manager.current = subscription(options.applicationServerKey, endpoint);
            return manager.current;
        }
    };
    return manager;
}

function notificationCaches() {
    const saved = new Map();
    return {
        saved,
        async open(name) {
            return {
                async put(url, response) { saved.set(`${name} ${url}`, await response.text()); },
                async match(url) {
                    const text = saved.get(`${name} ${url}`);
                    return text === undefined ? undefined : { async json() { return JSON.parse(text); } };
                },
                async delete(url) { return saved.delete(`${name} ${url}`); }
            };
        }
    };
}

function viewportHost({
    height = 844, visibleHeight = height, offsetTop = 0, scale = 1,
    top = 59, right = 0, bottom = 34, left = 0, standalone = false,
    active = { tagName: 'BODY' }
} = {}) {
    const inputs = [];
    const frames = new Map();
    const observers = [];
    let frameId = 0;
    const bounds = { height, bottom: height, width: 390, left: 0, top: 0 };
    const out = { getBoundingClientRect: () => bounds };
    const viewport = eventTarget({ height: visibleHeight, offsetTop, scale });
    const document = eventTarget({
        hidden: false,
        activeElement: active,
        documentElement: { dataset: {}, appendChild() {} },
        body: { appendChild: input => inputs.push(input) },
        getElementById: () => out,
        createElement: tag => eventTarget({
            tagName: tag.toUpperCase(), style: {}, value: '',
            classList: { add() {}, remove() {}, toggle() {} },
            setAttribute() {},
            setSelectionRange(start, end) { this.selectionStart = start; this.selectionEnd = end; }
        })
    });
    const window = eventTarget({
        visualViewport: viewport,
        requestAnimationFrame(callback) {
            const id = ++frameId;
            frames.set(id, callback);
            return id;
        }
    });
    const context = vm.createContext({
        document, window, navigator: { standalone }, queueMicrotask, Event,
        getComputedStyle: () => ({
            paddingTop: `${top}px`, paddingRight: `${right}px`,
            paddingBottom: `${bottom}px`, paddingLeft: `${left}px`
        }),
        ResizeObserver: class {
            constructor(callback) { observers.push(callback); }
            observe(element) { assert.equal(element, out); }
        }
    });
    vm.runInContext(source('browserHost.js').replace(/^export /gm, ''), context);
    const insets = [];
    return {
        context, document, window, viewport, bounds, frames, observers, inputs, insets,
        publish() { context.publishViewportInsets((...values) => insets.push(values)); },
        configure() { context.configureViewportInsets((...values) => insets.push(values)); },
        flush() {
            const pending = [...frames.values()];
            frames.clear();
            for (const callback of pending)
                callback();
        }
    };
}

test('portrait and landscape CSS safe areas retain their physical edge ordering', () => {
    for (const edges of [
        { top: 59, right: 0, bottom: 34, left: 0 },
        { top: 0, right: 59, bottom: 21, left: 59 }
    ]) {
        const app = viewportHost(edges);
        app.publish();
        assert.deepEqual(app.insets[0],
            [edges.top, edges.right, edges.bottom, edges.left, 0, 844]);
    }
});

test('Safari chrome and a stale dismissed keyboard are never treated as an active IME', () => {
    for (const active of [
        { tagName: 'BODY' },
        { tagName: 'TEXTAREA', readOnly: true },
        { tagName: 'INPUT', disabled: true }
    ]) {
        const app = viewportHost({ visibleHeight: 544, active });
        app.publish();
        assert.equal(app.insets[0][4], 0);
        assert.equal(app.insets[0][2], 34);
    }
});

test('keyboard overlap uses the current canvas and visual viewport offset without double shrinking', () => {
    for (const [height, visibleHeight, offsetTop, expected] of [
        [844, 544, 0, 300],
        [810, 510, 0, 300],
        [844, 510, 34, 300],
        [544, 544, 0, 0]
    ]) {
        const app = viewportHost({
            height, visibleHeight, offsetTop, active: { tagName: 'TEXTAREA' }
        });
        app.publish();
        assert.deepEqual(app.insets[0], [59, 0, 34, 0, expected, height]);
    }
});

test('standalone home-indicator exclusion and pinch zoom do not open a phantom keyboard', () => {
    for (const [visibleHeight, scale] of [[810, 1], [809.5, 1], [422, 2]]) {
        const app = viewportHost({
            visibleHeight, scale, standalone: true, active: { tagName: 'TEXTAREA' }
        });
        app.publish();
        assert.equal(app.insets[0][4], 0);
        assert.equal(app.document.documentElement.dataset.lumiStandalone, 'true');
    }
});

test('viewport updates coalesce after layout and cover focus, rotation, host resize and resume', () => {
    const app = viewportHost({ visibleHeight: 544, active: { tagName: 'TEXTAREA' } });
    app.configure();
    assert.equal(app.insets[0][4], 300);
    assert.equal(app.observers.length, 1);

    app.window.dispatch('resize');
    app.viewport.dispatch('resize');
    app.viewport.dispatch('scroll');
    app.bounds.height = app.bounds.bottom = 810;
    assert.equal(app.frames.size, 1);
    app.flush();
    assert.deepEqual(app.insets.at(-1), [59, 0, 34, 0, 266, 810]);

    app.document.activeElement = { tagName: 'BODY' };
    app.document.dispatch('focusout');
    app.flush();
    assert.equal(app.insets.at(-1)[4], 0);

    for (const notify of [
        () => app.window.dispatch('orientationchange'),
        () => app.window.dispatch('pageshow'),
        () => app.observers[0](),
        () => app.document.dispatch('focusin'),
        () => app.document.dispatch('visibilitychange')
    ]) {
        const previous = app.insets.length;
        notify();
        app.flush();
        assert.equal(app.insets.length, previous + 1);
    }

    app.document.hidden = true;
    app.document.dispatch('visibilitychange');
    assert.equal(app.frames.size, 0);
    app.document.hidden = false;
    app.document.dispatch('visibilitychange');
    app.flush();
    assert.equal(app.insets.at(-1)[4], 0);
});

test('Android browsers resize the app above the keyboard instead of panning it', () => {
    const viewport = source('index.html').match(/<meta name="viewport" content="([^"]+)"/)[1];
    assert.match(viewport, /interactive-widget=resizes-content/);

    const app = viewportHost({ height: 544, active: { tagName: 'TEXTAREA' } });
    app.publish();
    assert.deepEqual(app.insets[0], [59, 0, 34, 0, 0, 544]);
});

test('browsers without visualViewport keep only their safe area', () => {
    const app = viewportHost({ active: { tagName: 'TEXTAREA' } });
    app.window.visualViewport = undefined;
    app.publish();
    assert.deepEqual(app.insets[0], [59, 0, 34, 0, 0, 844]);
});

test('native editors prevent Safari focus zoom while preserving larger typography', () => {
    const app = viewportHost();
    for (const fontSize of [14, 16, 18]) {
        app.context.showNativeTextInput(
            1, 84, 742, 260, 48, 0, 0, 390, 844, '', 'Message Lumi',
            true, 0, 'text', 'send', false, true, true, true,
            'Inter', fontSize, 400, 'normal', 20, 0, 0, 0, 0, 0, 'left', 'ltr', false);
        assert.equal(app.inputs[0].style.fontSize, `${Math.max(16, fontSize)}px`);
    }
    assert.equal(app.inputs.length, 1);
    assert.ok(!/user-scalable\s*=\s*no|maximum-scale\s*=\s*1/i.test(source('index.html')));
});

test('canceling the native file picker resolves its wait and allows another selection', async () => {
    const app = viewportHost();
    const inputs = [];
    const removed = [];
    const storage = {
        StorageProvider: {
            openFileDialog(...args) {
                assert.equal(this, storage.StorageProvider);
                assert.deepEqual(args, ['start', true, null, false, true]);
                const input = eventTarget({
                    tagName: 'INPUT', type: 'file', files: [],
                    remove() { removed.push(this); }
                });
                inputs.push(input);
                return new Promise(resolve => {
                    input.addEventListener('change', () => resolve({ files: input.files }));
                    app.document.dispatch('click', { target: input });
                });
            }
        }
    };
    app.context.configureFilePickerCancellation(storage);
    for (let attempt = 0; attempt < 2; attempt++) {
        const pending = storage.StorageProvider.openFileDialog('start', true, null, false, true);
        inputs[attempt].dispatch('cancel');
        assert.deepEqual((await pending).files, []);
        assert.equal(removed.at(-1), inputs[attempt]);
    }
    const selected = storage.StorageProvider.openFileDialog('start', true, null, false, true);
    inputs[2].files = [{ name: 'chosen.png' }];
    inputs[2].dispatch('change');
    assert.deepEqual((await selected).files, [{ name: 'chosen.png' }]);
    assert.equal(removed.length, 3);
});

test('native picker errors still reach the caller without retaining its input or listeners', async () => {
    const app = viewportHost();
    let removed = false;
    const input = eventTarget({
        tagName: 'INPUT', type: 'file', files: [],
        remove() { removed = true; }
    });
    const storage = { StorageProvider: { openFileDialog() {
        app.document.dispatch('click', { target: input });
        throw new Error('Picker unavailable');
    } } };
    app.context.configureFilePickerCancellation(storage);
    await assert.rejects(storage.StorageProvider.openFileDialog(), /Picker unavailable/);
    assert.equal(removed, true);
    let changes = 0;
    input.addEventListener('change', () => changes++);
    input.dispatch('cancel');
    assert.equal(changes, 0, 'The canceled handler is removed after failure');
});

function startup(failedModule, { userAgent = 'Chrome/143.0.0.0', search = '', language } = {}) {
    const retry = eventTarget({ hidden: true });
    const label = { textContent: 'Opening Lumi...' };
    const errors = [];
    const modules = [];
    const lifecycle = [];
    const warnings = [];
    const installed = [];
    let reloads = 0;
    let recoveries = 0;
    let started = false;
    let culture;
    let runtimeConfig = {};
    const window = eventTarget({
        location: { href: `https://lumi.test/app/${search}`, search, reload: () => reloads++ }
    });
    const interop = {
        SetNativeTextInputText() {}, HandleNativeTextInputKey() {}, SetNativeTextInputFocus() {},
        SetApplicationActive() {}, CanApplyAppUpdate() {}, SetAppUpdateState() {}, SetNetworkAvailable() {},
        OpenChatFromNotification() {}
    };
    const notificationClicks = [];
    const runtime = {
        getConfig: () => ({ mainAssemblyName: 'Lumi.Mobile.Browser' }),
        getAssemblyExports: async () => ({ Lumi: { Mobile: { Browser: { BrowserInterop: interop } } } }),
        runMain: async () => { lifecycle.push('app-start'); started = true; }
    };
    const dotnet = {
        withDiagnosticTracing() { return this; },
        withApplicationCulture(value) { culture = value; return this; },
        withConfig(value) { runtimeConfig = { ...runtimeConfig, ...value }; return this; },
        withEnvironmentVariable() { return this; },
        withApplicationArgumentsFromQuery() { return this; },
        create: async () => { lifecycle.push('runtime-created'); return runtime; }
    };
    const context = vm.createContext({
        window, location: window.location, URLSearchParams, navigator: { onLine: true, userAgent },
        getDotnetRuntime: () => ({ runtimeBuildInfo: { wasmEnableThreads: false } }),
        lumiLocalization: language
            ? { language, ready: Promise.resolve(), text: (key, fallback) => fallback }
            : undefined,
        document: {
            getElementById: () => retry,
            querySelector: () => ({
                classList: { contains: () => false },
                querySelector: () => label
            })
        },
        console: { error: error => errors.push(error), warn: (...args) => warnings.push(args) },
        async loadModule(name) {
            modules.push(name);
            if (name === failedModule)
                throw new Error(`HTTP 503 loading ${name}`);
            if (name === './browserHost.js')
                return { configureBrowserRecovery() {}, configureNativeTextInputs() {}, configureViewportInsets() {},
                    configureFilePickerCancellation() { lifecycle.push('picker-prepared'); } };
            if (name === './_framework/storage.js') {
                lifecycle.push('storage-loaded');
                return { StorageProvider: {} };
            }
            if (name === './pwaHost.js')
                return {
                    checkAppUpdate() {}, reloadWebApp: () => recoveries++,
                    configureAppLifecycle() {}, configureAppUpdates() {},
                    configureNotificationClicks(open) {
                        lifecycle.push('notification-clicks');
                        notificationClicks.push(open);
                    }
                };
            if (name === './_framework/dotnet.js')
                return { dotnet };
            if (name === './workerFrames.js')
                return { installWorkerFrames: options => installed.push(options) };
            throw new Error(`Unexpected fixture module ${name}`);
        }
    });
    const main = source('main.js')
        .replace(/^import\s+\{([^}]+)\}\s+from\s+(['"])([^'"]+)\2;/gm,
            'const {$1} = await loadModule("$3");')
        .replace(/\bimport\s*\(/g, 'loadModule(');
    const ready = vm.runInContext(`(async () => { ${main} })()`, context)
        .catch(error => errors.push(error));
    return {
        retry, label, errors, modules, ready, installed, warnings, lifecycle, interop, notificationClicks,
        get started() { return started; },
        get reloads() { return reloads; },
        get recoveries() { return recoveries; },
        get culture() { return culture; },
        get runtimeConfig() { return runtimeConfig; }
    };
}

test('a failed PWA host import exposes the startup error and a working retry without that module', async () => {
    const app = startup('./pwaHost.js');
    await app.ready;
    assert.equal(app.retry.hidden, false);
    assert.match(app.label.textContent, /Lumi could not start:.*503.*pwaHost\.js/);
    assert.ok(!app.modules.includes('./_framework/dotnet.js'));
    app.retry.dispatch('click');
    assert.equal(app.reloads, 1);
    assert.equal(app.recoveries, 0);
});

test('a runtime import failure retains explicit PWA recovery once the host module loaded', async () => {
    const app = startup('./_framework/dotnet.js');
    await app.ready;
    assert.equal(app.retry.hidden, false);
    assert.match(app.label.textContent, /Lumi could not start:.*503.*dotnet\.js/);
    app.retry.dispatch('click');
    assert.equal(app.recoveries, 1);
    assert.equal(app.reloads, 0);
});

test('picker preparation requires the runtime, precedes app startup, and fails without preventing chatting', async () => {
    const ready = startup(null);
    await ready.ready;
    assert.deepEqual(ready.lifecycle, ['runtime-created', 'storage-loaded', 'picker-prepared', 'app-start', 'notification-clicks']);
    assert.equal(ready.errors.length, 0);
    assert.equal(ready.warnings.length, 0);
    const failed = startup('./_framework/storage.js');
    await failed.ready;
    assert.equal(failed.started, true);
    assert.equal(failed.errors.length, 0);
    assert.match(failed.warnings[0][0], /attachment picker could not be prepared/);
});

test('the runtime starts in the page language and loads every translated resource assembly', async () => {
    const english = startup(null);
    await english.ready;
    assert.equal(english.culture, 'en');
    assert.equal(english.runtimeConfig.loadAllSatelliteResources, true);
    const japanese = startup(null, { language: 'ja' });
    await japanese.ready;
    assert.equal(japanese.culture, 'ja');
    assert.equal(japanese.started, true);
    assert.equal(japanese.errors.length, 0);
});

test('Android Chromium enables worker rendering without diagnostic UI; other browsers keep their renderer', async () => {
    for (const [userAgent, search, expected] of [
        ['Android 15 Chrome/143.0.0.0', '', true],
        ['Android 15 SamsungBrowser/30 Chrome/143.0.0.0', '', true],
        ['Android 15 Firefox/143.0', '', false],
        ['Windows Chrome/143.0.0.0', '', false],
        ['iPhone Safari/605.1', '', false],
        ['Android 15 Chrome/143.0.0.0', '?workerframes=0', false],
        ['Windows Chrome/143.0.0.0', '?workerframes=1', true]
    ]) {
        const app = startup(null, { userAgent, search });
        await app.ready;
        assert.equal(app.installed.length, expected ? 1 : 0, `${userAgent} ${search}`);
        if (expected)
            assert.equal(app.installed[0].showStatus, search === '?workerframes=1');
        assert.equal(app.started, true);
        assert.equal(app.errors.length, 0);
        assert.ok(!app.modules.includes('./pwaDiagnostics.js'));
    }
});

test('a worker module download failure still starts the normal app and reports the fallback', async () => {
    const app = startup('./workerFrames.js', { userAgent: 'Android Chrome/143.0.0.0' });
    await app.ready;
    assert.equal(app.started, true);
    assert.equal(app.warnings.length, 1);
    assert.equal(app.errors.length, 0);
    assert.equal(app.retry.hidden, true);
});

test('notification taps reach the app only once it runs', async () => {
    const app = startup(null);
    await app.ready;
    assert.equal(app.notificationClicks.length, 1);
    assert.equal(app.notificationClicks[0], app.interop.OpenChatFromNotification);
    assert.ok(app.lifecycle.indexOf('notification-clicks') > app.lifecycle.indexOf('app-start'));
});

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
    assert.deepEqual(app.navigations, [`${origin}/app/?lumi-signin=1`]);
    assert.equal(app.context.recoverGatewaySignIn(), false);
    const reopened = host({ origin, storage: app.storage });
    assert.equal(reopened.context.recoverGatewaySignIn(), false);
    reopened.context.confirmGatewaySignIn();
    assert.equal(reopened.context.recoverGatewaySignIn(), true);
    assert.deepEqual(reopened.navigations, [`${origin}/app/?lumi-signin=1`]);
    assert.equal(app.registrations.length, 0, 'Sign-in must not rebuild the app cache');
});

test('gateway sign-in strips stale recovery markers without carrying unrelated query values', () => {
    const origin = 'https://private-47654.uks1.devtunnels.ms';
    const app = host({ origin });
    app.window.location.href = `${origin}/app/?lumi-refresh=1&workerframes=0&token=SECRET`;
    app.context.recoverGatewaySignIn();
    assert.deepEqual(app.navigations, [`${origin}/app/?lumi-signin=1&workerframes=0`]);
    app.window.location.href = `${origin}/app/?lumi-signin=1&lumi-refresh=1&other=kept`;
    app.context.configureAppLifecycle(() => {});
    assert.equal(app.window.location.href, `${origin}/app/?other=kept`);
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
    assert.equal(new URL(app.navigations[0]).search, '?lumi-signin=1');
    assert.equal(new URL(app.navigations[1]).search, '?lumi-refresh=1');
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

test('retry replaces a workerless registration instead of updating it forever', async () => {
    const app = host({ storage: new Map([['pairing-test', 'kept']]) });
    const states = [];
    app.context.configureAppUpdates(() => false, state => states.push(state));
    await app.context.registerAppCache();
    const failed = app.registration;
    failed.active = null;
    failed.update = async () => { throw new Error('The abandoned registration must not be updated'); };
    const worker = eventTarget({ state: 'installing' });
    const replacement = eventTarget({
        active: null, waiting: null, installing: worker, async update() {}
    });
    let registrations = 0;
    app.serviceWorker.register = async () => { registrations++; return replacement; };
    const checks = [app.context.checkAppUpdate(), app.context.checkAppUpdate()];
    await Promise.all(checks);
    assert.equal(registrations, 1);
    worker.state = 'redundant';
    replacement.installing = null;
    worker.dispatch('statechange');
    assert.equal(states.at(-1), 'failed');
    replacement.active = {};
    app.serviceWorker.controller = replacement.active;
    app.serviceWorker.dispatch('controllerchange');
    assert.equal(states.at(-1), 'current', 'A successful first controller clears the prior failure');
    assert.equal(app.storage.get('pairing-test'), 'kept');
    assert.equal(app.reloads, 0);
});

test('an update without an active or installing worker does not report a failed cache as current', async () => {
    const app = host();
    const states = [];
    app.context.configureAppUpdates(() => true, state => states.push(state));
    await app.context.registerAppCache();
    const worker = eventTarget({ state: 'installing' });
    app.registration.installing = worker;
    app.registration.dispatch('updatefound');
    app.registration.active = null;
    app.registration.installing = null;
    worker.state = 'redundant';
    worker.dispatch('statechange');
    await app.context.checkAppUpdate();
    assert.equal(states.at(-1), 'failed');
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

async function recovery({
    scope = 'https://lumi.test/app/', failure, registrationFailure, updateFailure,
    serviceWorker = true, query = '?lumi-refresh=1'
} = {}) {
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
            location: {
                origin: 'https://lumi.test', href: `https://lumi.test/app/${query}`,
                replace: url => navigations.push(url)
            },
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
                if (registrationFailure)
                    throw registrationFailure;
                return { waiting: worker, async update() {
                    updates++;
                    if (updateFailure)
                        throw updateFailure;
                } };
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
    assert.match(app.status.textContent, /Replacing the app registration/);
    assert.match(app.status.textContent, /Controller could not be removed/);
});

test('recovery reports the failing browser step instead of only a generic refresh error', async () => {
    for (const [options, stage, reason] of [
        [{ registrationFailure: new Error('Script fetch was redirected') },
            'Registering the current app', 'Script fetch was redirected'],
        [{ updateFailure: new Error('Storage is unavailable') },
            'Checking for the current build', 'Storage is unavailable']
    ]) {
        const app = await recovery(options);
        assert.equal(app.retry.hidden, false);
        assert.equal(app.navigations.length, 0);
        assert.equal(app.errors.length, 1);
        assert.ok(app.status.textContent.includes(stage));
        assert.ok(app.status.textContent.includes(reason));
        assert.match(app.status.textContent, /pairing is kept/);
    }
});

test('fresh recovery drops profiling and unrelated query values without touching pairing', async () => {
    const app = await recovery({ query: '?lumi-refresh=1&profile=1&token=private&returnTo=https://other.test/' });
    assert.deepEqual(app.navigations, ['https://lumi.test/app/']);
    const refresh = await recovery({ query: '?lumi-refresh=1&profile=refresh&token=SECRET' });
    assert.deepEqual(refresh.navigations, ['https://lumi.test/app/']);
    const normal = await recovery({ query: '?lumi-refresh=1&profile=0' });
    assert.deepEqual(normal.navigations, ['https://lumi.test/app/']);
});

test('exact worker-renderer overrides survive cache recovery and app reload without retaining other queries', async () => {
    const recovered = await recovery({
        query: '?lumi-refresh=1&workerframes=1&token=SECRET&returnTo=https://other.test/'
    });
    assert.deepEqual(recovered.navigations, ['https://lumi.test/app/?workerframes=1']);
    const combined = await recovery({ query: '?workerframes=1&profile=refresh' });
    assert.deepEqual(combined.navigations, ['https://lumi.test/app/?workerframes=1']);
    const disabled = await recovery({ query: '?workerframes=0' });
    assert.deepEqual(disabled.navigations, ['https://lumi.test/app/?workerframes=0']);
    const invalid = await recovery({ query: '?workerframes=true' });
    assert.deepEqual(invalid.navigations, ['https://lumi.test/app/']);
    const app = host();
    app.window.location.href = 'https://lumi.test/app/?workerframes=1&secret=private';
    app.context.reloadWebApp();
    assert.deepEqual(app.navigations, ['https://lumi.test/app/?lumi-refresh=1&workerframes=1']);
    app.window.location.href = 'https://lumi.test/app/?workerframes=0';
    app.context.reloadWebApp();
    assert.equal(app.navigations.at(-1), 'https://lumi.test/app/?lumi-refresh=1&workerframes=0');
});

function worker({
    installError, missingCache = false,
    scope = 'https://lumi.test/app/', networkError, networkStatus = 200, networkType = 'basic',
    appClients = [], legacyFramework, assets, cacheAsset, notificationText, maxActions
} = {}) {
    const handlers = new Map();
    const requests = [];
    const network = [];
    const deleted = [];
    const networkOptions = [];
    const warnings = [];
    const notifications = [];
    const pendingSends = [];
    const displayed = new Map();
    const openedWindows = [];
    let claims = 0;
    let skips = 0;
    const cache = {
        async add(request) {
            requests.push(request);
            if (installError)
                throw installError;
            await cacheAsset?.(request);
        },
        async match(url) {
            return missingCache ? undefined : { cached: url };
        }
    };
    const notificationCache = {
        async match(url) {
            return notificationText && url === new URL('notification-text.json', scope).href
                ? { async json() { return notificationText; } }
                : undefined;
        },
        async put(url, response) {
            assert.equal(url, new URL('pending-send.json', scope).href);
            pendingSends.push(JSON.parse(await response.text()));
        }
    };
    const self = {
        // Browsers that show no buttons on notifications (Safari) have no maxActions.
        ...(maxActions === undefined ? {} : { Notification: { maxActions } }),
        registration: {
            scope,
            async showNotification(title, options) {
                notifications.push({ title, options });
                // As browsers do, a notification with the same tag replaces the one showing.
                displayed.set(options.tag, { title, ...options });
            },
            async getNotifications({ tag } = {}) {
                return [...displayed.values()].filter(shown => tag === undefined || shown.tag === tag);
            }
        },
        lumiAssetVersion: 'build2',
        lumiAssets: assets ?? [
            { url: 'index.html', hash: 'sha256-shell' },
            { url: 'main.js', hash: 'sha256-main' },
            { url: '_framework/runtime.wasm', hash: 'sha256-wasm' }
        ],
        addEventListener: (name, callback) => handlers.set(name, callback),
        clients: {
            async claim() { claims++; },
            async matchAll() { return appClients; },
            async openWindow(url) { openedWindows.push(url); }
        },
        skipWaiting() { skips++; }
    };
    vm.runInNewContext(source('service-worker.js'), {
        self, URL, Request, Response, Set,
        importScripts: url => assert.equal(url, './service-worker-assets.js'),
        caches: {
            async open(name) {
                if (name === 'lumi-notifications')
                    return notificationCache;
                if (name === 'lumi-pwa-build1')
                    return { async match(url) { return url === legacyFramework ? { legacy: url } : undefined; } };
                assert.equal(name, 'lumi-pwa-build2');
                return cache;
            },
            async keys() { return ['lumi-pwa-build1', 'lumi-pwa-build2', 'lumi-notifications', 'unrelated-cache']; },
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
        requests, network, deleted, networkOptions, warnings, notifications, openedWindows, pendingSends,
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
        async push(message) {
            let pending;
            handlers.get('push')({
                data: message === undefined ? null : {
                    json() {
                        if (message instanceof Error)
                            throw message;
                        return message;
                    }
                },
                waitUntil(value) { pending = value; }
            });
            await pending;
        },
        async click(data, action = '') {
            let pending;
            let closed = 0;
            handlers.get('notificationclick')({
                action,
                notification: { data, close() { closed++; } },
                waitUntil(value) { pending = value; }
            });
            await pending;
            return closed;
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
    assert.equal(app.warnings.length, 3);
    assert.equal(app.deleted.length, 0, 'A failed update must not clear an existing cache');
});

test('Safari cache installation uses single-asset adds with at most four in flight', async () => {
    const assets = Array.from({ length: 9 }, (_, i) => ({
        url: `asset-${i}.js`, hash: `sha256-test${i}`
    }));
    const pending = [];
    let inFlight = 0;
    let peak = 0;
    const app = worker({
        assets,
        cacheAsset: () => new Promise(resolve => {
            inFlight++;
            peak = Math.max(peak, inFlight);
            pending.push(() => { inFlight--; resolve(); });
        })
    });
    const install = app.lifecycle('install');
    await settle();
    assert.equal(app.requests.length, 4);
    pending.shift()();
    await settle();
    assert.equal(app.requests.length, 4, 'Finish the current batch before starting the next');
    pending.splice(0).forEach(resolve => resolve());
    await settle();
    assert.equal(app.requests.length, 8);
    pending.splice(0).forEach(resolve => resolve());
    await settle();
    assert.equal(app.requests.length, 9);
    pending.splice(0).forEach(resolve => resolve());
    await install;
    assert.equal(peak, 4);
    assert.equal(inFlight, 0);
    assert.equal(app.claims, 0);
    assert.equal(app.skips, 0);
});

test('a later asset failure stops installation before any further batch or activation', async () => {
    const assets = Array.from({ length: 9 }, (_, i) => ({
        url: `asset-${i}.js`, hash: `sha256-test${i}`
    }));
    const app = worker({
        assets,
        cacheAsset: async request => {
            if (request.url.endsWith('/asset-4.js'))
                throw new Error('Integrity mismatch');
        }
    });
    await assert.rejects(app.lifecycle('install'), /Integrity mismatch/);
    assert.equal(app.requests.length, 8);
    assert.equal(app.claims, 0);
    assert.equal(app.skips, 0);
    assert.equal(app.deleted.length, 0);
    assert.equal(app.warnings.length, 1);
    assert.match(app.warnings[0][0], /asset-4\.js/);
});

test('activation removes only obsolete Lumi app caches', async () => {
    const app = worker();
    await app.lifecycle('activate');
    assert.deepEqual(app.deleted, ['lumi-pwa-build1']);
    assert.equal(app.claims, 1);
});

test('a pushed reply shows in the app\'s language, one notification per chat that rings again', async () => {
    const app = worker({ notificationText: { lang: 'he', replyFallback: 'סיים לענות.' } });

    await app.push({
        id: `reply:${notifiedChat}`, kind: 'reply', chatId: notifiedChat, title: 'Trip plan',
        agent: 'Travel Lumi', text: 'Three hotels fit.', host: 'LIGHTO', at: 1793000000000
    });
    await app.push({
        id: `reply:${notifiedChat}`, kind: 'reply', chatId: notifiedChat, title: 'Trip plan', text: '', at: 1793000060000
    });

    const [first, second] = app.notifications.map(plain);
    assert.equal(first.title, 'Trip plan');
    assert.deepEqual(first.options, {
        body: 'Travel Lumi: Three hotels fit.',
        tag: notifiedChat,
        renotify: true,
        timestamp: 1793000000000,
        lang: 'he',
        icon: 'https://lumi.test/app/icons/lumi-192.png',
        badge: 'https://lumi.test/app/icons/lumi-badge-96.png',
        data: { chatId: notifiedChat, title: 'Trip plan', at: 1793000000000 }
    });
    assert.equal(second.options.body, 'סיים לענות.');
    assert.equal(second.options.tag, notifiedChat);
    assert.equal(second.options.renotify, true);
    assert.equal(second.options.timestamp, 1793000060000);
});

test('a push that arrives late never replaces what a chat shows about something newer', async () => {
    const otherChat = '0d6f2c3a-1b2c-4d5e-8f90-a1b2c3d4e5f6';
    const app = worker();

    await app.push({ kind: 'approval', chatId: notifiedChat, title: 'Trip plan', text: 'Run setup.ps1', host: 'LIGHTO', at: 2000 });
    // A reply the push service held while the phone slept arrives after the approval that followed it.
    await app.push({ kind: 'reply', chatId: notifiedChat, title: 'Trip plan', text: 'Done.', host: 'LIGHTO', at: 1000 });
    await app.push({ kind: 'reply', chatId: otherChat, title: 'Groceries', text: 'List ready.', host: 'LIGHTO', at: 500 });
    await app.push({ kind: 'reply', chatId: notifiedChat, title: 'Trip plan', text: 'All set.', host: 'LIGHTO', at: 3000 });

    const [approval, late, other, newest] = app.notifications.map(plain);
    assert.equal(approval.options.body, 'Needs your approval on LIGHTO: Run setup.ps1');
    // Every push must show a notification, so the late one shows the approval again, quietly.
    assert.equal(late.title, 'Trip plan');
    assert.equal(late.options.body, approval.options.body);
    assert.equal(late.options.tag, notifiedChat);
    assert.equal(late.options.renotify, false);
    assert.equal(late.options.timestamp, 2000);
    assert.deepEqual(late.options.data, approval.options.data);
    // Other chats keep their own notifications.
    assert.equal(other.options.body, 'List ready.');
    assert.equal(other.options.renotify, true);
    // Something newer replaces it and rings again.
    assert.equal(newest.options.body, 'All set.');
    assert.equal(newest.options.renotify, true);
});

test('questions, forms, approvals and tests say what Lumi waits for, in English until the app saves its words', async () => {
    const app = worker();

    await app.push({ kind: 'question', chatId: notifiedChat, title: 'Trip plan', text: 'Which city first?', host: 'LIGHTO' });
    await app.push({ kind: 'input', chatId: notifiedChat, title: 'Trip plan', text: 'Trip details', host: 'LIGHTO' });
    await app.push({ kind: 'approval', chatId: notifiedChat, title: 'Trip plan', text: 'Run setup.ps1', host: 'LIGHTO' });
    await app.push({ kind: 'test', id: 'test:1', title: '', text: '', host: 'LIGHTO' });

    const shown = app.notifications.map(plain);
    assert.deepEqual(shown.map(notification => notification.options.body), [
        'Which city first?',
        'Needs your input: Trip details',
        'Needs your approval on LIGHTO: Run setup.ps1',
        'This is how LIGHTO will reach you while you\'re away.'
    ]);
    assert.equal(shown[3].title, 'Notifications work');
    assert.equal(shown[3].options.tag, 'lumi-test');
    assert.equal(shown[3].options.data.chatId, null);
    assert.equal(shown[0].options.lang, 'en');
});

test('the worker\'s English wording is the app\'s own', () => {
    const catalog = readFileSync(
        new URL('../../src/Lumi.Mobile/Localization/ChatSessionStrings.resx', import.meta.url), 'utf8');
    const worker = source('service-worker.js');
    for (const [field, key] of [
        ['testTitle', 'NotificationTestTitle'],
        ['testText', 'NotificationTestText'],
        ['inputText', 'NotificationInputText'],
        ['approvalText', 'NotificationApprovalText'],
        ['replyFallback', 'NotificationReplyFallback']
    ]) {
        const expected = catalog.match(new RegExp(`<data name="${key}"[^>]*><value>([^<]*)</value>`))[1];
        const actual = worker.match(new RegExp(`${field}: '((?:[^'\\\\]|\\\\.)*)'`))[1].replace(/\\'/g, '\'');
        assert.equal(actual, expected, field);
    }
});

test('an unreadable push still shows a notification, as browsers require', async () => {
    const app = worker();

    await app.push(new Error('Not JSON'));
    await app.push(undefined);

    assert.equal(app.notifications.length, 2);
    assert.equal(app.notifications[0].title, 'Lumi');
    assert.equal(app.notifications[0].options.body, '');
    assert.equal(app.notifications[0].options.tag, 'lumi');
    assert.equal(app.warnings.length, 1);
});

test('tapping a notification brings the open app forward on its chat', async () => {
    const focused = [];
    const posted = [];
    const client = (url, focusError) => ({
        url,
        async focus() {
            if (focusError)
                throw focusError;
            focused.push(url);
            return this;
        },
        postMessage: message => posted.push({ url, message })
    });
    const app = worker({
        appClients: [client('https://lumi.test/elsewhere/'), client('https://lumi.test/app/?workerframes=1')]
    });

    const closed = await app.click({ chatId: notifiedChat, title: 'Trip plan' });

    assert.equal(closed, 1);
    assert.deepEqual(focused, ['https://lumi.test/app/?workerframes=1']);
    assert.deepEqual(plain(posted), [{
        url: 'https://lumi.test/app/?workerframes=1',
        message: { type: 'lumi-open-chat', chatId: notifiedChat, title: 'Trip plan' }
    }]);
    assert.deepEqual(app.openedWindows, []);

    // A window the browser will not bring forward still opens the chat.
    const stubborn = worker({ appClients: [client('https://lumi.test/app/', new Error('Not allowed'))] });
    await stubborn.click({ chatId: notifiedChat, title: 'Trip plan' });
    assert.equal(posted.length, 2);
    assert.equal(stubborn.warnings.length, 1);
});

test('tapping a notification with the app closed opens it on that chat', async () => {
    const app = worker();

    await app.click({ chatId: notifiedChat, title: 'Trip plan' });
    await app.click({ chatId: null, title: '' });

    assert.deepEqual(app.openedWindows, [`https://lumi.test/app/?lumi-chat=${notifiedChat}`, 'https://lumi.test/app/']);
});

test('a reply offers its suggestions as buttons, as many as the browser shows', async () => {
    const reply = {
        kind: 'reply', chatId: notifiedChat, title: 'Trip plan', text: 'Three hotels fit.', host: 'LIGHTO', at: 2000,
        suggestions: ['Book the Dan', 'Compare prices', 'Show me more']
    };
    const app = worker({ maxActions: 2 });

    await app.push(reply);
    await app.push({ ...reply, kind: 'question', chatId: '0d6f2c3a-1b2c-4d5e-8f90-a1b2c3d4e5f6', at: 2500 });
    // An older push that arrives late shows the reply again, buttons and all.
    await app.push({ kind: 'approval', chatId: notifiedChat, title: 'Trip plan', text: 'Run setup.ps1', host: 'LIGHTO', at: 1000 });

    const [shown, question, late] = app.notifications.map(plain);
    assert.deepEqual(shown.options.actions, [
        { action: 'suggest:0', title: 'Book the Dan' },
        { action: 'suggest:1', title: 'Compare prices' }
    ]);
    assert.deepEqual(shown.options.data.suggestions, ['Book the Dan', 'Compare prices']);
    assert.equal(question.options.actions, undefined);
    assert.deepEqual(late.options.actions, shown.options.actions);
    assert.deepEqual(late.options.data, shown.options.data);

    // A browser without notification buttons, like Safari, shows the reply alone.
    const safari = worker();
    await safari.push(reply);
    assert.equal(plain(safari.notifications[0]).options.actions, undefined);
    assert.equal(plain(safari.notifications[0]).options.data.suggestions, undefined);
});

test('picking a suggestion leaves it for the app, which opens on its chat', async () => {
    const data = { chatId: notifiedChat, title: 'Trip plan', at: 2000, suggestions: ['Book the Dan', 'Compare prices'] };
    const app = worker({ maxActions: 2 });

    await app.click(data, 'suggest:1');
    // A tap on the notification itself, or a button it does not have, sends nothing.
    await app.click(data);
    await app.click(data, 'suggest:7');

    assert.deepEqual(app.pendingSends.map(({ chatId, text }) => ({ chatId, text })), [{ chatId: notifiedChat, text: 'Compare prices' }]);
    assert.equal(typeof app.pendingSends[0].at, 'number');
    assert.deepEqual(app.openedWindows, Array(3).fill(`https://lumi.test/app/?lumi-chat=${notifiedChat}`));
});

test('the app sends a picked suggestion in its chat once it is in front, once, and only soon after', async () => {
    const caches = notificationCaches();
    const pending = 'lumi-notifications https://lumi.test/app/pending-send.json';
    const leave = (chatId, at = Date.now()) =>
        caches.saved.set(pending, JSON.stringify({ chatId, text: 'Compare prices', at }));
    const opened = [];
    const sent = [];
    const app = host({ hidden: true, notifications: { caches } });
    app.context.configureNotificationClicks(chatId => opened.push(chatId), (chatId, text) => sent.push([chatId, text]));
    const flush = () => new Promise(resolve => setImmediate(resolve));
    const tap = async () => {
        app.serviceWorker.dispatch('message', { data: { type: 'lumi-open-chat', chatId: notifiedChat, title: 'Trip plan' } });
        await flush();
    };

    // On a locked phone the app may already be told, but nothing goes until it is in front.
    leave(notifiedChat);
    await tap();
    assert.deepEqual(opened, [notifiedChat]);
    assert.deepEqual(sent, []);
    app.document.hidden = false;
    app.document.dispatch('visibilitychange');
    await flush();
    assert.deepEqual(sent, [[notifiedChat, 'Compare prices']]);
    assert.equal(caches.saved.has(pending), false);

    // Opening the chat again sends nothing more, nor does a suggestion for another chat, or an old one.
    await tap();
    leave('0d6f2c3a-1b2c-4d5e-8f90-a1b2c3d4e5f6');
    await tap();
    leave(notifiedChat, Date.now() - 6 * 60 * 1000);
    await tap();
    assert.deepEqual(sent, [[notifiedChat, 'Compare prices']]);
    assert.equal(caches.saved.has(pending), false);
});

test('notification support says what a browser needs first', () => {
    const support = (options, secure = true) =>
        host({ secure, notifications: { userAgent: 'Android 15 Chrome/143.0.0.0', ...options } }).context.getNotificationSupport();

    assert.equal(support({}), 'supported');
    assert.equal(support({}, false), 'insecure');
    assert.equal(support({ userAgent: 'iPhone OS 18 Safari/605.1' }), 'home-screen');
    assert.equal(support({ userAgent: 'iPhone OS 18 Safari/605.1', standalone: true }), 'supported');
    assert.equal(support({ userAgent: 'Macintosh Safari/605.1', platform: 'MacIntel', maxTouchPoints: 5 }), 'home-screen');
    assert.equal(support({ userAgent: 'Macintosh Safari/605.1', platform: 'MacIntel', maxTouchPoints: 0 }), 'supported');
    assert.equal(support({ supported: false }), 'unsupported');
});

test('turning notifications on asks the browser and reports its answer', async () => {
    const app = host({ notifications: { permission: 'default', answer: 'granted' } });
    assert.equal(app.context.getNotificationPermission(), 'default');
    assert.equal(await app.context.requestNotificationPermission(), 'granted');
    assert.equal(app.context.getNotificationPermission(), 'granted');

    const without = host({ notifications: { supported: false } });
    assert.equal(without.context.getNotificationPermission(), 'denied');
    assert.equal(await without.context.requestNotificationPermission(), 'denied');
});

test('subscribing leaves the wording for the worker and keeps one subscription for the PC\'s key', async () => {
    const manager = pushManager();
    const caches = notificationCaches();
    const app = host({ notifications: { permission: 'granted', caches, pushManager: manager } });

    const first = JSON.parse(await app.context.subscribeToPush(pcKey, '{"lang":"he"}'));
    const again = JSON.parse(await app.context.subscribeToPush(pcKey, '{"lang":"he"}'));

    assert.deepEqual(first, { endpoint: 'https://fcm.googleapis.com/fcm/send/browser', p256dh: 'BBrowserKey', auth: 'Secret' });
    assert.deepEqual(again, first);
    assert.equal(manager.calls.subscribed.length, 1);
    assert.equal(manager.calls.subscribed[0].userVisibleOnly, true);
    assert.deepEqual(
        Array.from(manager.calls.subscribed[0].applicationServerKey),
        Array.from(Buffer.from(pcKey, 'base64url')));
    assert.equal(caches.saved.get('lumi-notifications https://lumi.test/app/notification-text.json'), '{"lang":"he"}');
});

test('a subscription for another key is replaced, and a browser without a push service is refused', async () => {
    const replaced = pushManager({ existing: { key: new Uint8Array(65).fill(9).buffer } });
    const app = host({ notifications: { permission: 'granted', caches: notificationCaches(), pushManager: replaced } });
    await app.context.subscribeToPush(pcKey, '{}');
    assert.equal(replaced.calls.unsubscribed, 1);
    assert.equal(replaced.calls.subscribed.length, 1);

    const removed = pushManager({ endpoint: 'https://permanently-removed.invalid/fcm/send/browser' });
    const edge = host({ notifications: { permission: 'granted', caches: notificationCaches(), pushManager: removed } });
    await assert.rejects(edge.context.subscribeToPush(pcKey, '{}'), /no working push service/);
    assert.equal(removed.calls.unsubscribed, 1);
});

test('turning notifications off ends the browser\'s subscription', async () => {
    const manager = pushManager({ existing: { key: Buffer.from(pcKey, 'base64url') } });
    const app = host({ notifications: { pushManager: manager } });

    await app.context.unsubscribeFromPush();
    await app.context.unsubscribeFromPush();

    assert.equal(manager.calls.unsubscribed, 1);
});

test('opening a chat takes down only that chat\'s notifications', async () => {
    const closed = [];
    const shown = ['chat-1', 'chat-2', 'chat-1'].map((tag, index) => ({ tag, close: () => closed.push(`${tag}#${index}`) }));
    const app = host({ notifications: { shown } });

    app.context.clearChatNotifications('chat-1');
    await settle();

    assert.deepEqual(closed, ['chat-1#0', 'chat-1#2']);
});

test('a tapped notification opens its chat from the worker, or from the address it opened', () => {
    const opened = [];
    const app = host({ notifications: {} });
    app.window.location.href = `https://lumi.test/app/?workerframes=1&lumi-chat=${notifiedChat}`;

    app.context.configureNotificationClicks((chatId, title) => opened.push([chatId, title]));
    assert.deepEqual(opened, [[notifiedChat, '']]);
    assert.equal(app.window.location.href, 'https://lumi.test/app/?workerframes=1');
    assert.equal(app.serviceWorker.messagesStarted, true);

    app.serviceWorker.dispatch('message', { data: { type: 'lumi-open-chat', chatId: notifiedChat, title: 'Trip plan' } });
    app.serviceWorker.dispatch('message', { data: { type: 'lumi-open-chat', chatId: 'javascript:alert(1)' } });
    app.serviceWorker.dispatch('message', { data: { type: 'lumi-other', chatId: notifiedChat } });
    assert.deepEqual(opened, [[notifiedChat, ''], [notifiedChat, 'Trip plan']]);
});

test('the worker tells the open app about every push it showed, so a test can confirm delivery', async () => {
    const posted = [];
    const client = (url, fails = false) => ({
        url,
        postMessage(message) {
            if (fails)
                throw new Error('The page is closing.');
            posted.push({ url, message });
        }
    });
    const app = worker({
        appClients: [client('https://lumi.test/app/'), client('https://lumi.test/elsewhere/'), client('https://lumi.test/app/?workerframes=1', true)]
    });

    await app.push({ kind: 'test', id: 'test:1', title: '', text: '', host: 'LIGHTO', at: 5000 });
    await app.push({ kind: 'reply', chatId: notifiedChat, title: 'Trip plan', text: 'Done.', host: 'LIGHTO', at: 2000 });
    // A late push that shows the chat's newer notification again still arrived.
    await app.push({ kind: 'reply', chatId: notifiedChat, title: 'Trip plan', text: 'Older.', host: 'LIGHTO', at: 1000 });

    assert.deepEqual(plain(posted), ['test', 'reply', 'reply'].map(kind => ({
        url: 'https://lumi.test/app/',
        message: { type: 'lumi-push-received', kind }
    })));
    // A page that can't be told doesn't keep the notification from showing.
    assert.equal(app.notifications.length, 3);
    assert.equal(app.warnings.length, 3);
});

test('a test counts as delivered only once the worker reports a test push, and as lost when none comes', async () => {
    const app = host({ notifications: {} });

    const arrived = app.context.expectTestPush(20000);
    assert.equal(app.serviceWorker.messagesStarted, true);
    app.serviceWorker.dispatch('message', { data: { type: 'lumi-push-received', kind: 'reply' } });
    app.serviceWorker.dispatch('message', { data: { type: 'lumi-open-chat', kind: 'test' } });
    app.serviceWorker.dispatch('message', { data: { type: 'lumi-push-received', kind: 'test' } });
    assert.equal(await arrived, true);
    assert.equal(app.timers.size, 0);

    const lost = app.context.expectTestPush(20000);
    const [timer] = [...app.timers.values()];
    assert.equal(timer.ms, 20000);
    timer.callback();
    assert.equal(await lost, false);
    // Each wait stops listening when it ends.
    app.serviceWorker.dispatch('message', { data: { type: 'lumi-push-received', kind: 'test' } });
    assert.equal(app.timers.size, 0);
});

test('an app opened from the Home Screen counts as installed; a browser tab does not', () => {
    assert.equal(host({ notifications: {} }).context.isInstalledApp(), false);
    assert.equal(host({ notifications: { standalone: true } }).context.isInstalledApp(), true);
    const safari = host({ notifications: {} });
    safari.context.navigator.standalone = true;
    assert.equal(safari.context.isInstalledApp(), true);
});

test('each browser pairs under its own name, so the PC can tell side-by-side browsers apart', () => {
    const app = viewportHost();
    const name = (userAgent, extra = {}) => {
        Object.assign(app.context.navigator, { userAgent, platform: '', maxTouchPoints: 0, ...extra });
        return app.context.getDeviceName();
    };

    assert.equal(name('Mozilla/5.0 (Linux; Android 15; SM-S928B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/28.0 Chrome/130.0.0.0 Mobile Safari/537.36'), 'Samsung Internet (Android)');
    assert.equal(name('Mozilla/5.0 (Linux; Android 15; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Mobile Safari/537.36 EdgA/143.0.0.0'), 'Edge (Android)');
    assert.equal(name('Mozilla/5.0 (Linux; Android 15; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Mobile Safari/537.36'), 'Chrome (Android)');
    assert.equal(name('Mozilla/5.0 (Linux; Android 15) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Mobile Safari/537.36 OPR/85.0'), 'Opera (Android)');
    assert.equal(name('Mozilla/5.0 (Android 15; Mobile; rv:143.0) Gecko/143.0 Firefox/143.0'), 'Firefox (Android)');
    assert.equal(name('Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1'), 'Safari (iPhone)');
    assert.equal(name('Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/143.0 Mobile/15E148 Safari/604.1'), 'Chrome (iPhone)');
    assert.equal(name('Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15', { platform: 'MacIntel', maxTouchPoints: 5 }), 'Safari (iPad)');
    assert.equal(name('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0'), 'Edge (Windows)');
    assert.equal(name(''), '');
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

test('Microsoft sign-in navigation reaches the gateway without intercepting or rebuilding the app', async () => {
    const origin = 'https://private-47654.uks1.devtunnels.ms';
    const app = worker({ scope: origin + '/app/' });
    assert.equal(await app.fetch(origin + '/app/?lumi-signin=1', { mode: 'navigate' }), undefined);
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
        'manifest.webmanifest', 'service-worker.js', '_framework/dotnet.js',
        'workerFrames.js', 'workerFramePresenter.js']) {
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
    for (const name of [...assets.map(asset => asset.url), 'service-worker-assets.js']) {
        const file = path.join(publishRoot, ...name.split('/'));
        for (const [extension, decompress] of [['.br', brotliDecompressSync], ['.gz', gunzipSync]]) {
            if (existsSync(file + extension))
                assert.deepEqual(decompress(readFileSync(file + extension)), readFileSync(file), name + extension);
        }
    }
    const inventoryBytes = content.slice(0, content.indexOf('self.lumiAssetVersion'));
    assert.equal(self.lumiAssetVersion, createHash('sha256').update(inventoryBytes).digest('hex').toUpperCase());
});
