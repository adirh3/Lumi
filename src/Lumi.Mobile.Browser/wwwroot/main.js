import {
    configureNativeTextInputs,
    publishViewportInsets
} from './browserHost.js';
import {
    configureAppLifecycle,
    configureAppUpdates,
    checkAppUpdate,
    reloadWebApp
} from './pwaHost.js';

document.getElementById('startup-retry').addEventListener('click', reloadWebApp);
void checkAppUpdate();

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
    const { dotnet } = await import('./_framework/dotnet.js');
    const runtime = await dotnet
        .withDiagnosticTracing(false)
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

    const publishInsets = () => publishViewportInsets(interop.SetViewportInsets);
    window.addEventListener('resize', publishInsets);
    window.visualViewport?.addEventListener('resize', publishInsets);
    window.visualViewport?.addEventListener('scroll', publishInsets);
    publishInsets();
} catch (error) {
    showFatalError(error);
}
