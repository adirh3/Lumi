import {
    configureNativeTextInputs,
    publishViewportInsets
} from './browserHost.js';

const showFatalError = error => {
    console.error(error);
    const splash = document.querySelector('.lumi-splash');
    if (!splash)
        return;
    splash.querySelector('span').textContent =
        `Lumi could not start: ${error?.message || error}`;
};

window.addEventListener('error', event => showFatalError(event.error || event.message));
window.addEventListener('unhandledrejection', event => showFatalError(event.reason));

try {
    const [{ dotnet }, { loadRuntimeResource }] = await Promise.all([
        import('./_framework/dotnet.js'),
        import('./runtimeResourceCache.js')
    ]);
    const runtime = await dotnet
        .withDiagnosticTracing(false)
        .withApplicationArgumentsFromQuery()
        .withResourceLoader(loadRuntimeResource)
        .create();
    const config = runtime.getConfig();

    await runtime.runMain(config.mainAssemblyName, [globalThis.location.href]);

    const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
    const interop = exports.Lumi.Mobile.Browser.BrowserInterop;
    configureNativeTextInputs(
        interop.SetNativeTextInputText,
        interop.HandleNativeTextInputKey,
        interop.SetNativeTextInputFocus);

    const publishLifecycle = () => interop.SetApplicationActive(!document.hidden);
    document.addEventListener('visibilitychange', publishLifecycle);
    window.addEventListener('pageshow', () => interop.SetApplicationActive(true));
    window.addEventListener('pagehide', () => interop.SetApplicationActive(false));

    const publishInsets = () => publishViewportInsets(interop.SetViewportInsets);
    window.addEventListener('resize', publishInsets);
    window.visualViewport?.addEventListener('resize', publishInsets);
    window.visualViewport?.addEventListener('scroll', publishInsets);
    publishLifecycle();
    publishInsets();
} catch (error) {
    showFatalError(error);
}
