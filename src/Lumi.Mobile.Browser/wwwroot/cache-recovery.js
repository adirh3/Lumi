const uiText = (key, fallback, ...args) =>
    globalThis.lumiLocalization?.text(key, fallback, ...args) ?? fallback;

function waitForWorkerState(worker, state) {
    return new Promise((resolve, reject) => {
        const finish = error => {
            clearTimeout(timeout);
            worker.removeEventListener('statechange', check);
            if (error)
                reject(error);
            else
                resolve();
        };
        const check = () => {
            if (worker.state === state || worker.state === 'activated')
                finish();
            else if (worker.state === 'redundant')
                finish(new Error(uiText('WebUpdateInstallFailed', 'The web app update could not be installed.')));
        };
        const timeout = setTimeout(() =>
            finish(new Error(uiText('WebUpdateTimeout', 'The web app update took too long.'))), 60000);
        worker.addEventListener('statechange', check);
        check();
    });
}

async function recoverCachedApp() {
    await globalThis.lumiLocalization?.ready;
    const retry = document.getElementById('cache-recovery-retry');
    const status = document.getElementById('cache-recovery-status');
    let stage = uiText('WebFindingRegistration', 'Finding the app registration');
    retry.hidden = true;
    status.textContent = uiText('WebRefreshing', 'Refreshing the web app...');
    try {
        const app = new URL('/app/', window.location.origin);
        if ('serviceWorker' in navigator) {
            const registration = await navigator.serviceWorker.getRegistration(app.href);
            if (registration?.scope === app.href) {
                stage = uiText('WebReplacingRegistration', 'Replacing the app registration');
                await registration.unregister();
            }
            stage = uiText('WebRegistering', 'Registering the current app');
            status.textContent = uiText('WebUpdatingRuntime', 'Updating the cached runtime...');
            const fresh = await navigator.serviceWorker.register(new URL('service-worker.js', app).href, {
                scope: app.href,
                updateViaCache: 'none'
            });
            stage = uiText('WebCheckingBuild', 'Checking for the current build');
            await fresh.update();
            const worker = fresh.waiting ?? fresh.installing;
            if (worker) {
                stage = uiText('WebInstallingFiles', 'Installing the verified app files');
                await waitForWorkerState(worker, 'installed');
                stage = uiText('WebActivatingUpdate', 'Activating the app update');
                worker.postMessage({ type: 'lumi-activate-recovery' });
                await waitForWorkerState(worker, 'activated');
            }
        }
        // Keep cookies, pairing and verified caches; only this page gets a fresh controller.
        stage = uiText('WebOpeningStage', 'Opening Lumi');
        const destination = new URL(app.href);
        const workerFrames = new URL(window.location.href).searchParams.get('workerframes');
        if (workerFrames === '1' || workerFrames === '0')
            destination.searchParams.set('workerframes', workerFrames);
        window.location.replace(destination.href);
    } catch (error) {
        console.error(`[Lumi PWA] Browser recovery failed: ${stage}.`, error);
        status.textContent =
            uiText('WebRefreshFailed',
                `Lumi could not refresh the web app. Your pairing is kept. ${stage}: ${error?.message || String(error)} Try again.`,
                stage, error?.message || String(error));
        retry.hidden = false;
    }
}

document.getElementById('cache-recovery-retry').addEventListener('click', recoverCachedApp);
void recoverCachedApp();
