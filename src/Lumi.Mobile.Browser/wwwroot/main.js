import {
    configureNativeTextInputs,
    configureViewportInsets
} from './browserHost.js';
let retryStartup = () => window.location.reload();
document.getElementById('startup-retry').addEventListener('click', () => retryStartup());

const showFatalError = error => {
    console.error(error);
    const splash = document.querySelector('.lumi-splash');
    if (!splash || splash.classList.contains('splash-close'))
        return;
    splash.querySelector('span').textContent =
        navigator.onLine
            ? `Lumi could not start: ${error?.message || error}`
            : 'You are offline. Connect to the internet and try again.';
    document.getElementById('startup-retry').hidden = false;
};

window.addEventListener('error', event => showFatalError(event.error || event.message));
window.addEventListener('unhandledrejection', event => showFatalError(event.reason));

try {
    const {
        configureAppLifecycle,
        configureAppUpdates,
        checkAppUpdate,
        reloadWebApp
    } = await import('./pwaHost.js');
    retryStartup = reloadWebApp;
    void checkAppUpdate();

    const { dotnet } = await import('./_framework/dotnet.js');
    const runtime = await dotnet
        .withDiagnosticTracing(false)
        // A larger young generation collects ~3x less often while scrolling and switching chats;
        // each collection is a main-thread pause that would otherwise drop frames.
        .withEnvironmentVariable('MONO_GC_PARAMS', 'nursery-size=16m')
        .withApplicationArgumentsFromQuery()
        .create();
    const config = runtime.getConfig();

    await runtime.runMain(config.mainAssemblyName, [globalThis.location.href]);

    const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
    const interop = exports.Lumi.Mobile.Browser.BrowserInterop;
    configureNativeTextInputs(
        interop.SetNativeTextInputText,
        interop.HandleNativeTextInputKey,
        interop.SetNativeTextInputFocus);

    configureAppLifecycle(interop.SetApplicationActive);
    configureAppUpdates(interop.CanApplyAppUpdate, interop.SetAppUpdateState);

    // Reconnect as soon as the network returns instead of waiting out a retry pause.
    window.addEventListener('online', () => interop.SetNetworkAvailable(true));
    window.addEventListener('offline', () => interop.SetNetworkAvailable(false));
    if (!navigator.onLine)
        interop.SetNetworkAvailable(false);

    configureViewportInsets(interop.SetViewportInsets);
} catch (error) {
    showFatalError(error);
}
