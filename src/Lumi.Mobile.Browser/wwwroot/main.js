import {
    configureBrowserRecovery,
    configureFilePickerCancellation,
    configureNativeTextInputs,
    configureViewportInsets,
    hasPendingNativeInput,
    showBrowserRecovery
} from './browserHost.js';

window.dispatchEvent(new Event('lumi-pwa-main'));

const workerDirectory = /^\/app\/render-worker\/[0-9a-f]{64}\/index\.html$/;
const workerPage = workerDirectory.test(location.pathname);
const search = new URLSearchParams(location.search);
const workerFrameMode = search.get('workerframes');
const androidChromium = /Android/.test(navigator.userAgent) && /Chrome\//.test(navigator.userAgent);
let retryStartup = () => window.location.reload();
let workerStarted = false;
let workerFailed = false;
let startupTimeout;
const uiText = (key, fallback, ...args) =>
    globalThis.lumiLocalization?.text(key, fallback, ...args) ?? fallback;

function useCopyFreeRenderer() {
    const destination = new URL(location.href);
    destination.pathname = '/app/';
    destination.searchParams.set('renderer', 'copy-free');
    destination.searchParams.delete('lumi-refresh');
    window.location.replace(destination.href);
}

async function openDefaultRenderer() {
    if (workerPage || search.get('renderer') === 'copy-free' || workerFrameMode !== null ||
        !androidChromium || !window.isSecureContext ||
        (!navigator.onLine && !navigator.serviceWorker?.controller) ||
        typeof Worker !== 'function' || typeof OffscreenCanvas !== 'function' ||
        typeof HTMLCanvasElement.prototype.transferControlToOffscreen !== 'function')
        return false;

    try {
        const response = await fetch('./render-worker-entry.json', {
            mode: 'same-origin', redirect: 'error', signal: AbortSignal.timeout(2500)
        });
        if (response.status === 404)
            return false; // Desktop packages without a worker payload still support the ordinary app.
        if (!response.ok)
            throw new Error(`Could not locate the render worker (HTTP ${response.status}).`);
        const { entry } = await response.json();
        if (typeof entry !== 'string' || !workerDirectory.test(`/app/${entry}`))
            throw new Error('Invalid render worker entry.');
        if (navigator.serviceWorker && !navigator.serviceWorker.controller) {
            const { registerAppCache } = await import('./pwaHost.js');
            // On a fresh install let the small shell cache claim the page so it can also
            // cache the first worker download. A blocked service worker must not block launch.
            await new Promise(resolve => {
                const timer = setTimeout(resolve, 3000);
                void registerAppCache().then(registration => registration && navigator.serviceWorker.ready)
                    .catch(error => console.warn('[Lumi PWA] Could not prepare the offline cache.', error))
                    .finally(() => { clearTimeout(timer); resolve(); });
            });
        }
        const destination = new URL(`/app/${entry}`, location.origin);
        destination.search = location.search;
        window.location.replace(destination.href);
        return true;
    } catch (error) {
        console.warn('[Lumi PWA] The render worker is unavailable; using copy-free rendering.', error);
        return false;
    }
}

function reportWorkerFailure(error) {
    if (workerFailed)
        return;
    workerFailed = true;
    console.error('[Lumi PWA] Render worker failed.', error);
    if (workerStarted) {
        // The user may be composing. Recovery must be explicit, not an automatic reload.
        retryStartup = useCopyFreeRenderer;
        showBrowserRecovery(uiText('WebContextLost', 'Your browser stopped displaying Lumi. Reload to reconnect.'));
    } else {
        useCopyFreeRenderer();
    }
}

configureBrowserRecovery(() => retryStartup());
document.getElementById('startup-retry').addEventListener('click', () => retryStartup());

const showFatalError = error => {
    console.error(error);
    const splash = document.querySelector('.lumi-splash');
    if (!splash || splash.classList.contains('splash-close'))
        return;
    const message = String(error?.message || error);
    splash.querySelector('span').textContent =
        !navigator.onLine
            ? uiText('WebOffline', 'You are offline. Connect to the internet and try again.')
            : uiText('WebStartFailed', `Lumi could not start: ${message}`, message);
    document.getElementById('startup-retry').hidden = false;
};

window.addEventListener('error', event => showFatalError(event.error || event.message));
window.addEventListener('unhandledrejection', event => showFatalError(event.reason));

// Only the threaded document gets a render-worker watchdog. Do not start a second CLR in that document.
if (workerPage) {
    window.addEventListener('lumi-render-worker-error', event => reportWorkerFailure(event.detail));
    window.addEventListener('lumi-render-worker-context-lost', () =>
        reportWorkerFailure(new Error('WebGL context lost.')));
}

const notify = callback => (...args) => {
    const result = callback(...args);
    if (typeof result?.then === 'function')
        void result.catch(error => console.error('[Lumi PWA] Browser event failed.', error));
    return result;
};

async function start() {
    await globalThis.lumiLocalization?.ready;
    if (await openDefaultRenderer())
        return;
    const {
        configureAppLifecycle,
        configureAppUpdates,
        configureNotificationClicks,
        configureWebAppInstallation,
        checkAppUpdate,
        reloadWebApp,
        tryApplyAppUpdate
    } = await import('./pwaHost.js');
    retryStartup = reloadWebApp;
    if (!workerPage)
        void checkAppUpdate();

    const splash = document.querySelector('.lumi-splash');
    if (workerPage) {
        // Keep the composer inaccessible until the worker has a usable canvas and the browser bridges are wired.
        splash.classList.remove('avalonia-splash');
        document.documentElement.dataset.lumiRenderBoot = 'true';
        retryStartup = useCopyFreeRenderer;
        startupTimeout = setTimeout(() => reportWorkerFailure(new Error('Worker download timed out.')), 6 * 60_000);
    }
    const canvas = workerPage ? new Promise(resolve => {
        window.addEventListener('lumi-render-worker-canvas', event => resolve(event.detail), { once: true });
    }) : undefined;
    let { dotnet } = await import('./_framework/dotnet.js');
    let verifyRuntime;
    if (workerPage) {
        const { configureRenderWorker, verifyRenderWorkerRuntime } = await import('./renderWorkerRuntime.js');
        verifyRuntime = verifyRenderWorkerRuntime;
        const status = splash.querySelector('span');
        const opening = status.textContent;
        dotnet = configureRenderWorker(dotnet, (loaded, total) => {
            if (total)
                status.textContent = `${opening} (${loaded}/${total})`;
        });
    }

    const runtime = await dotnet
        .withDiagnosticTracing(false)
        .withApplicationCulture(globalThis.lumiLocalization?.language ?? 'en')
        // Standalone WASM otherwise leaves the UI's satellite resource assemblies unloaded.
        .withConfig({ loadAllSatelliteResources: true })
        // A larger young generation collects ~3x less often while scrolling and switching chats;
        // each collection is a main-thread pause that would otherwise drop frames.
        .withEnvironmentVariable('MONO_GC_PARAMS', 'nursery-size=16m')
        .withApplicationArgumentsFromQuery()
        .create();
    if (workerPage) {
        clearTimeout(startupTimeout);
        if (workerFailed)
            return;
        startupTimeout = setTimeout(() => reportWorkerFailure(new Error('Render worker startup timed out.')), 30000);
        verifyRuntime(runtime);
    }
    const config = runtime.getConfig();
    const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
    const interop = exports.Lumi.Mobile.Browser.BrowserInterop;

    // Avalonia's storage module resolves managed exports when it loads, so the runtime must exist.
    try {
        configureFilePickerCancellation(await import('./_content/Avalonia.Browser/storage.js'));
    } catch (error) {
        console.warn('[Lumi PWA] The attachment picker could not be prepared.', error);
    }

    if (!workerPage && (workerFrameMode === '1' || workerFrameMode === 'clock' ||
        (workerFrameMode !== '0' && androidChromium))) {
        try {
            const { installWorkerFrames } = await import('./workerFrames.js');
            installWorkerFrames({
                threaded: globalThis.getDotnetRuntime(0).runtimeBuildInfo.wasmEnableThreads,
                showStatus: workerFrameMode === '1',
                presentBitmaps: workerFrameMode === '1'
            });
        } catch (error) {
            console.warn('[Lumi PWA] Worker renderer could not be loaded; using normal rendering.', error);
        }
    }

    configureNativeTextInputs(
        notify(interop.SetNativeTextInputText),
        workerPage ? interop.HandleNativeTextInputKeyAsync : interop.HandleNativeTextInputKey,
        notify(interop.SetNativeTextInputFocus),
        tryApplyAppUpdate);

    await runtime.runMain(config.mainAssemblyName, [globalThis.location.href]);
    if (workerPage && await canvas !== 'webgl2')
        throw new Error('The render worker requires WebGL 2.');

    // Only an explicitly instrumented fixture build exports this control channel.
    if (search.get('smoothness') === '1') {
        const benchmark = exports.Lumi.Mobile.Browser.BrowserSmoothness;
        if (!benchmark)
            throw new Error('The mobile smoothness harness was not compiled into this build.');
        globalThis.lumiSmoothness = {
            command: async request => JSON.parse(await benchmark.Command(JSON.stringify(request)))
        };
        for (const name of ['pointerdown', 'pointerup'])
            window.addEventListener(name, event => {
                if (event.isTrusted && event.pointerType === 'touch') {
                    const box = document.getElementById('out').getBoundingClientRect();
                    void notify(benchmark.ObserveTouch)(event.clientX - box.left, event.clientY - box.top, name === 'pointerdown');
                }
            }, { capture: true, passive: true });
    }

    configureAppLifecycle(notify(interop.SetApplicationActive));
    const nativeInputIsSettled = () => !workerFailed && !hasPendingNativeInput();
    configureAppUpdates(
        async () => nativeInputIsSettled() && await interop.CanApplyAppUpdate() === true && nativeInputIsSettled(),
        notify(interop.SetAppUpdateState));
    configureNotificationClicks(notify(interop.OpenChatFromNotification), notify(interop.SendFromNotification));
    configureWebAppInstallation(notify(interop.WebAppInstallationChanged));

    // Reconnect as soon as the network returns instead of waiting out a retry pause.
    const setNetworkAvailable = notify(interop.SetNetworkAvailable);
    window.addEventListener('online', () => setNetworkAvailable(true));
    window.addEventListener('offline', () => setNetworkAvailable(false));
    if (!navigator.onLine)
        setNetworkAvailable(false);

    configureViewportInsets(notify(interop.SetViewportInsets));
    if (workerPage && !workerFailed) {
        workerStarted = true;
        retryStartup = reloadWebApp;
        delete document.documentElement.dataset.lumiRenderBoot;
        splash.classList.add('splash-close');
        // Don't compete with the first worker download by also precaching the copy-free runtime.
        void checkAppUpdate();
    }
}

try {
    await start();
} catch (error) {
    if (workerPage)
        reportWorkerFailure(error);
    else
        showFatalError(error);
} finally {
    if (startupTimeout !== undefined)
        clearTimeout(startupTimeout);
}
