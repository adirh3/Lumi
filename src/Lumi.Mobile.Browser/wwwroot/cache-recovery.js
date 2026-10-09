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
                finish(new Error('The web app update could not be installed.'));
        };
        const timeout = setTimeout(() =>
            finish(new Error('The web app update took too long.')), 60000);
        worker.addEventListener('statechange', check);
        check();
    });
}

async function recoverCachedApp() {
    const retry = document.getElementById('cache-recovery-retry');
    retry.hidden = true;
    try {
        const app = new URL('/app/', window.location.origin);
        if ('serviceWorker' in navigator) {
            const registration = await navigator.serviceWorker.getRegistration(app.href);
            if (registration?.scope === app.href)
                await registration.unregister();
            document.getElementById('cache-recovery-status').textContent = 'Updating the cached runtime...';
            const fresh = await navigator.serviceWorker.register(new URL('service-worker.js', app).href, {
                scope: app.href,
                updateViaCache: 'none'
            });
            await fresh.update();
            const worker = fresh.waiting ?? fresh.installing;
            if (worker) {
                await waitForWorkerState(worker, 'installed');
                worker.postMessage({ type: 'lumi-activate-recovery' });
                await waitForWorkerState(worker, 'activated');
            }
        }
        // Keep cookies, pairing and verified caches; only this page gets a fresh controller.
        window.location.replace(app.href);
    } catch (error) {
        console.error('[Lumi PWA] Browser recovery failed.', error);
        document.getElementById('cache-recovery-status').textContent =
            'Lumi could not refresh the web app. Your pairing is kept. Try again.';
        retry.hidden = false;
    }
}

document.getElementById('cache-recovery-retry').addEventListener('click', recoverCachedApp);
void recoverCachedApp();
