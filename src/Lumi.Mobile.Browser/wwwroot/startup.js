// This module is served from the ordinary app, even when the versioned worker module graph fails.
const worker = /^\/app\/render-worker\/[0-9a-f]{64}\/index\.html$/.test(location.pathname);
const failure = error => {
    console.error('[Lumi PWA] Could not load the app modules.', error);
    if (worker) {
        const fallback = new URL(location.href);
        fallback.pathname = '/app/';
        fallback.searchParams.set('renderer', 'copy-free');
        location.replace(fallback.href);
    } else {
        const retry = document.getElementById('startup-retry');
        retry.hidden = false;
        retry.addEventListener('click', () => location.reload(), { once: true });
        const status = document.querySelector('.lumi-splash span');
        const message = error?.message ?? String(error);
        status.textContent = globalThis.lumiLocalization?.text('WebStartFailed', 'Lumi could not start: {0}', message)
            ?? `Lumi could not start: ${message}`;
    }
};

if (worker) {
    const timer = setTimeout(() => failure(new Error('The worker entry module did not load.')), 30000);
    window.addEventListener('lumi-pwa-main', () => clearTimeout(timer), { once: true });
}

void import(new URL('./main.js', location.href)).catch(failure);
