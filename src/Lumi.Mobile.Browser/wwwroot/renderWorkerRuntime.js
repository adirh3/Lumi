// dotnet/runtime#136492: .NET 11's pool reads worker.loaded, but the loader sets info.isLoaded.
function markReadyWorkers(module) {
    const pool = module.PThread;
    if (typeof pool?.loadWasmModuleToWorker !== 'function')
        throw new Error('Unsupported .NET worker pool.');

    const load = pool.loadWasmModuleToWorker;
    pool.loadWasmModuleToWorker = async function (worker) {
        const result = await load.call(this, worker);
        if (worker.info?.isLoaded !== true)
            throw new Error('The .NET worker did not report readiness.');
        worker.loaded = true;
        return result;
    };
}

export function configureRenderWorker(dotnet, onDownloadResourceProgress) {
    if (!crossOriginIsolated || typeof SharedArrayBuffer !== 'function')
        throw new Error('The server did not enable cross-origin isolation for the render worker.');

    return dotnet.withModuleConfig({ preRun: markReadyWorkers, onDownloadResourceProgress })
        .withConfig({
            jsThreadBlockingMode: 'ThrowWhenBlockingWait',
            pthreadPoolInitialSize: 16,
            pthreadPoolUnusedSize: 4
        });
}

export function verifyRenderWorkerRuntime({ runtimeBuildInfo: build }) {
    if (!build.wasmEnableThreads || build.gitHash !== '297acf930fb0f5c07fd4e51d78e521b4ad2e5d1f')
        throw new Error('Unexpected threaded .NET runtime; use the copy-free renderer.');
}
