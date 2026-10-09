const darkPreference = window.matchMedia('(prefers-color-scheme: dark)');
let theme = 'System';
const gatewaySignInAttemptKey = 'lumi.pwa.gateway-sign-in-attempt.v1';
let appActive = !document.hidden;
let registrationPromise;
let appRegistration;
let updateCheck;
let runtimeController = navigator.serviceWorker?.controller;
let canApplyUpdate;
let onUpdateStateChanged;
let updateState = 'current';
let applyQueued = false;
let activationPending = false;
let activationTimeout;
let reloadPending = false;
let reloading = false;
const observedWorkers = new WeakSet();

function publishUpdateState(state) {
    if (state === updateState)
        return;
    updateState = state;
    onUpdateStateChanged?.(state);
}

function reportUpdateFailure(error) {
    console.warn('[Lumi PWA] The update could not finish; the current app and pairing are kept.', error);
    if (!reloadPending && !activationPending && !appRegistration?.waiting)
        publishUpdateState('failed');
}

export function configureAppUpdates(isSafeToApply, onStateChanged) {
    canApplyUpdate = isSafeToApply;
    onUpdateStateChanged = onStateChanged;
    onStateChanged(updateState);
    tryApplyAppUpdate();
}

export function tryApplyAppUpdate() {
    if (applyQueued || reloading || !reloadPending && !appRegistration?.waiting)
        return;

    // Property notifications can arrive halfway through restoring a draft or starting a send.
    applyQueued = true;
    queueMicrotask(() => {
        applyQueued = false;
        if (!appActive || document.hidden || !canApplyUpdate?.() || reloading)
            return;

        if (reloadPending) {
            reloading = true;
            publishUpdateState('applying');
            window.location.reload();
            return;
        }

        const worker = appRegistration?.waiting;
        if (!worker || activationPending)
            return;

        activationPending = true;
        publishUpdateState('applying');
        activationTimeout = setTimeout(() => {
            activationPending = false;
            console.warn('[Lumi PWA] The verified update did not activate in time.');
            publishUpdateState('failed');
        }, 60000);
        try {
            worker.postMessage({ type: 'lumi-activate-update' });
        } catch (error) {
            clearTimeout(activationTimeout);
            activationPending = false;
            console.warn('[Lumi PWA] The verified update could not be activated.', error);
            publishUpdateState('failed');
        }
    });
}

function observeWorker(worker) {
    if (!worker || observedWorkers.has(worker))
        return;

    observedWorkers.add(worker);
    const changed = () => {
        if (worker.state === 'installed' && appRegistration.waiting) {
            publishUpdateState('ready');
            tryApplyAppUpdate();
        } else if (worker.state === 'installing' && runtimeController) {
            publishUpdateState('downloading');
        } else if (worker.state === 'redundant') {
            reportUpdateFailure(new Error('The verified app assets could not be installed.'));
        }
    };
    worker.addEventListener('statechange', changed);
    changed();
}

export async function checkAppUpdate() {
    if (updateCheck)
        return updateCheck;

    updateCheck = (async () => {
        const registration = await registerAppCache();
        if (!registration)
            return;
        try {
            await registration.update();
            observeWorker(registration.installing);
            if (registration.waiting) {
                publishUpdateState('ready');
                tryApplyAppUpdate();
            } else if (!registration.installing && !reloadPending && !activationPending) {
                publishUpdateState('current');
            }
        } catch (error) {
            reportUpdateFailure(error);
        }
    })();
    try {
        await updateCheck;
    } finally {
        updateCheck = undefined;
    }
}

function applyTheme() {
    const dark = theme === 'Dark' || theme === 'System' && darkPreference.matches;
    const color = dark ? '#111114' : '#f7f7fa';
    document.querySelector('meta[name="theme-color"]').content = color;
    document.documentElement.style.colorScheme = dark ? 'dark' : 'light';
    document.documentElement.dataset.lumiTheme = dark ? 'dark' : 'light';
}

export function setTheme(preference) {
    theme = preference === 'Light' || preference === 'Dark' ? preference : 'System';
    applyTheme();
}

darkPreference.addEventListener('change', applyTheme);
applyTheme();

export function reloadWebApp() {
    const url = new URL('/app/', window.location.origin);
    url.searchParams.set('lumi-refresh', '1');
    window.location.assign(url.href);
}

export function recoverGatewaySignIn() {
    const origin = new URL(window.location.origin);
    if (origin.protocol !== 'https:' || !origin.hostname.endsWith('.devtunnels.ms'))
        return false;

    try {
        if (window.sessionStorage.getItem(gatewaySignInAttemptKey))
            return false;
        window.sessionStorage.setItem(gatewaySignInAttemptKey, '1');
    } catch (error) {
        console.warn('[Lumi PWA] Automatic Microsoft sign-in recovery is unavailable; use Sign in again.', error);
        return false;
    }

    reloadWebApp();
    return true;
}

export function confirmGatewaySignIn() {
    try {
        window.sessionStorage.removeItem(gatewaySignInAttemptKey);
    } catch (error) {
        console.warn('[Lumi PWA] The Microsoft sign-in recovery guard could not be cleared.', error);
    }
}

export function configureAppLifecycle(onActiveChanged) {
    // The shared shell starts active and already initiates its first handshake.
    let active = true;
    let pageVisible = true;
    const publish = () => {
        const next = pageVisible && !document.hidden;
        if (next === active)
            return;
        active = next;
        appActive = active;
        onActiveChanged(active);
        if (active) {
            tryApplyAppUpdate();
            void checkAppUpdate();
        }
    };
    document.addEventListener('visibilitychange', publish);
    window.addEventListener('pageshow', () => {
        pageVisible = true;
        publish();
    });
    window.addEventListener('pagehide', () => {
        pageVisible = false;
        publish();
    });
    window.launchQueue?.setConsumer(() => {
        publish();
        if (active)
            void checkAppUpdate();
    });
    publish();

    const url = new URL(window.location.href);
    if (url.searchParams.has('lumi-refresh')) {
        url.searchParams.delete('lumi-refresh');
        window.history.replaceState(window.history.state, '', url.href);
    }
}

export async function registerAppCache() {
    if (!('serviceWorker' in navigator) || !window.isSecureContext)
        return;

    if (registrationPromise)
        return registrationPromise;

    registrationPromise = (async () => {
        try {
            const registration = await navigator.serviceWorker.register('./service-worker.js', {
                scope: './',
                updateViaCache: 'none'
            });
            appRegistration = registration;
            registration.addEventListener('updatefound', () => observeWorker(registration.installing));
            navigator.serviceWorker.addEventListener('controllerchange', () => {
                const controller = navigator.serviceWorker.controller;
                if (!controller || controller === runtimeController)
                    return;
                if (!runtimeController && !activationPending) {
                    runtimeController = controller;
                    return;
                }

                runtimeController = controller;
                clearTimeout(activationTimeout);
                activationPending = false;
                reloadPending = true;
                publishUpdateState('ready');
                tryApplyAppUpdate();
            });
            window.addEventListener('online', () => void checkAppUpdate());
            observeWorker(registration.installing);
            if (registration.waiting) {
                publishUpdateState('ready');
                tryApplyAppUpdate();
            }
            return registration;
        } catch (error) {
            registrationPromise = undefined;
            console.warn('[Lumi PWA] App caching is unavailable; launches will need the network.', error);
            publishUpdateState('failed');
        }
    })();
    const registration = await registrationPromise;
    if (!registration)
        registrationPromise = undefined;
    return registration;
}
