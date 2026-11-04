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
            } else if (registration.active && !registration.installing && !reloadPending && !activationPending) {
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

function navigateToApp(marker) {
    const url = new URL('/app/', window.location.origin);
    url.searchParams.set(marker, '1');
    const workerFrames = new URL(window.location.href).searchParams.get('workerframes');
    if (workerFrames === '1' || workerFrames === '0')
        url.searchParams.set('workerframes', workerFrames);
    window.location.assign(url.href);
}

export function reloadWebApp() {
    navigateToApp('lumi-refresh');
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

    navigateToApp('lumi-signin');
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
    if (url.searchParams.has('lumi-refresh') || url.searchParams.has('lumi-signin')) {
        url.searchParams.delete('lumi-refresh');
        url.searchParams.delete('lumi-signin');
        window.history.replaceState(window.history.state, '', url.href);
    }
}

export async function registerAppCache() {
    if (!('serviceWorker' in navigator) || !window.isSecureContext)
        return;

    if (registrationPromise) {
        if (!appRegistration || appRegistration.active || appRegistration.waiting || appRegistration.installing)
            return registrationPromise;
        // A failed first install can leave a registration whose update() always rejects.
        registrationPromise = undefined;
        appRegistration = undefined;
    }

    registrationPromise = (async () => {
        try {
            const registration = await navigator.serviceWorker.register('./service-worker.js', {
                scope: './',
                updateViaCache: 'none'
            });
            appRegistration = registration;
            registration.addEventListener('updatefound', () => observeWorker(registration.installing));
            navigator.serviceWorker.addEventListener('controllerchange', onAppControllerChanged);
            window.addEventListener('online', onAppOnline);
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

function onAppControllerChanged() {
    const controller = navigator.serviceWorker.controller;
    if (!controller || controller === runtimeController)
        return;
    if (!runtimeController && !activationPending) {
        runtimeController = controller;
        publishUpdateState('current');
        return;
    }

    runtimeController = controller;
    clearTimeout(activationTimeout);
    activationPending = false;
    reloadPending = true;
    publishUpdateState('ready');
    tryApplyAppUpdate();
}

function onAppOnline() {
    void checkAppUpdate();
}

// ── Notifications (Web Push) ─────────────────────────────────────────────────────────────
// The PC pushes notifications through this browser's own push service; the service worker shows
// them. The page asks for permission, subscribes with the PC's key and leaves the words
// notifications use, in the app's language, where the service worker finds them.

const notificationCache = 'lumi-notifications';
const workerReadyTimeout = 120000;

export function getNotificationSupport() {
    if (!window.isSecureContext)
        return 'insecure';
    const appleMobile = /iPad|iPhone|iPod/.test(navigator.userAgent ?? '')
        || navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1;
    if (appleMobile && !isInstalledApp())
        return 'home-screen';
    return 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window
        ? 'supported'
        : 'unsupported';
}

export function getNotificationPermission() {
    return 'Notification' in window ? window.Notification.permission : 'denied';
}

/** Opened from the Home Screen (or as an installed app) rather than in a browser tab. */
export function isInstalledApp() {
    return window.matchMedia('(display-mode: standalone)').matches || navigator.standalone === true;
}

/**
 * Resolves true when the service worker reports that a test notification arrived and was shown,
 * false when none arrives in time. Watching starts at once, before the test is sent.
 */
export function expectTestPush(timeoutMs) {
    return new Promise(resolve => {
        const worker = navigator.serviceWorker;
        if (!worker) {
            resolve(false);
            return;
        }
        const finish = arrived => {
            clearTimeout(timer);
            worker.removeEventListener('message', received);
            resolve(arrived);
        };
        const received = event => {
            if (event.data?.type === 'lumi-push-received' && event.data.kind === 'test')
                finish(true);
        };
        const timer = setTimeout(() => finish(false), timeoutMs);
        worker.addEventListener('message', received);
        worker.startMessages?.();
    });
}

export async function requestNotificationPermission() {
    if (!('Notification' in window))
        return 'denied';
    return await window.Notification.requestPermission();
}

async function pushRegistration() {
    const registration = await registerAppCache();
    if (!registration)
        throw new Error('The app service worker is unavailable.');
    // Subscribing needs an active worker; a first visit may still be installing the app.
    let timeout;
    try {
        await Promise.race([
            navigator.serviceWorker.ready,
            new Promise((_, reject) => {
                timeout = setTimeout(
                    () => reject(new Error('The app service worker did not start.')),
                    workerReadyTimeout);
            })
        ]);
    } finally {
        clearTimeout(timeout);
    }
    return registration;
}

function base64UrlBytes(value) {
    const base64 = value.replace(/-/g, '+').replace(/_/g, '/');
    const binary = atob(base64 + '='.repeat((4 - base64.length % 4) % 4));
    return Uint8Array.from(binary, character => character.charCodeAt(0));
}

function sameKey(current, expected) {
    if (!current)
        return false;
    const bytes = new Uint8Array(current);
    return bytes.length === expected.length && bytes.every((value, index) => value === expected[index]);
}

export async function subscribeToPush(serverKey, notificationText) {
    const registration = await pushRegistration();
    const cache = await caches.open(notificationCache);
    await cache.put(
        new URL('notification-text.json', registration.scope).href,
        new Response(notificationText, { headers: { 'Content-Type': 'application/json' } }));

    const applicationServerKey = base64UrlBytes(serverKey);
    let subscription = await registration.pushManager.getSubscription();
    if (subscription && !sameKey(subscription.options?.applicationServerKey, applicationServerKey)) {
        // Made for another key, so the PC's pushes would be refused.
        await subscription.unsubscribe();
        subscription = null;
    }
    subscription ??= await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey });

    const { endpoint, keys } = subscription.toJSON();
    // A browser without a working push service can still hand out an address nothing reaches.
    if (!endpoint || new URL(endpoint).hostname.endsWith('.invalid')) {
        await subscription.unsubscribe().catch(() => {});
        throw new Error('This browser has no working push service.');
    }
    return JSON.stringify({ endpoint, p256dh: keys?.p256dh ?? '', auth: keys?.auth ?? '' });
}

export async function unsubscribeFromPush() {
    const registration = await navigator.serviceWorker?.getRegistration('./');
    const subscription = await registration?.pushManager?.getSubscription();
    await subscription?.unsubscribe();
}

export function clearChatNotifications(tag) {
    void (async () => {
        const registration = await navigator.serviceWorker?.getRegistration('./');
        for (const notification of await registration?.getNotifications?.({ tag }) ?? [])
            notification.close();
    })().catch(error => console.warn('[Lumi PWA] Could not take down notifications.', error));
}

// A suggestion picked in a reply's notification, which the service worker left for the app. It goes in
// the chat it was offered for, only once the app is in front (so never from a locked phone), and only
// shortly after the tap.
const pendingSendLifetimeMs = 5 * 60 * 1000;

async function takePendingSend(chatId) {
    if (typeof caches === 'undefined')
        return null;
    try {
        const cache = await caches.open(notificationCache);
        const url = new URL('pending-send.json', window.location.href).href;
        const saved = await cache.match(url);
        if (!saved)
            return null;
        await cache.delete(url);
        const pending = await saved.json();
        return pending?.chatId === chatId
            && typeof pending.text === 'string'
            && typeof pending.at === 'number'
            && Date.now() - pending.at < pendingSendLifetimeMs
            ? pending.text
            : null;
    } catch (error) {
        console.warn('[Lumi PWA] A suggestion from a notification could not be read.', error);
        return null;
    }
}

function whenVisible(callback) {
    if (!document.hidden) {
        callback();
        return;
    }
    const shown = () => {
        if (document.hidden)
            return;
        document.removeEventListener('visibilitychange', shown);
        callback();
    };
    document.addEventListener('visibilitychange', shown);
}

export function configureNotificationClicks(openChat, sendFromNotification) {
    const open = (chatId, title) => {
        if (typeof chatId !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(chatId))
            return;
        openChat(chatId, typeof title === 'string' ? title : '');
        whenVisible(() => void takePendingSend(chatId).then(text => {
            if (text)
                sendFromNotification(chatId, text);
        }));
    };
    // A tap on a notification while the app is open arrives from the service worker.
    navigator.serviceWorker?.addEventListener('message', event => {
        if (event.data?.type === 'lumi-open-chat')
            open(event.data.chatId, event.data.title);
    });
    navigator.serviceWorker?.startMessages?.();

    // A tap that had to open the app names the chat in the address.
    const url = new URL(window.location.href);
    const chatId = url.searchParams.get('lumi-chat');
    if (chatId !== null) {
        url.searchParams.delete('lumi-chat');
        window.history.replaceState(window.history.state, '', url.href);
        open(chatId, '');
    }
}
