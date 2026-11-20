import { TimerHelper, WebRenderTargetRegistry } from './_framework/avalonia.js';

export * from './_framework/avalonia.js';

if (typeof document === 'undefined') {
    // A render-side Dispatcher.Post must wake this worker's owning document, not its own JS realm.
    TimerHelper.setTimeout = delay => {
        self.postMessage({ lumiRenderWorker: 'wake', delay });
        return 0;
    };

    const initialize = WebRenderTargetRegistry.initializeWorker;
    WebRenderTargetRegistry.initializeWorker = function () {
        initialize.call(this);
        const receive = self.onmessage;
        // Avalonia replaces an existing onmessage handler. Observe the canvas before that handler
        // creates its context, regardless of the order in which the runtime registered listeners.
        self.onmessage = event => {
            const { data } = event;
            if (data?.avaloniaCmd === 'registerCanvas' && data.canvas instanceof OffscreenCanvas) {
                data.canvas.addEventListener('webglcontextlost', () =>
                    self.postMessage({ lumiRenderWorker: 'context-lost' }));
                const getContext = data.canvas.getContext;
                data.canvas.getContext = function (kind, ...options) {
                    const context = getContext.call(this, kind, ...options);
                    if (context) {
                        delete this.getContext;
                        self.postMessage({ lumiRenderWorker: 'canvas', kind });
                    }
                    return context;
                };
            }
            receive.call(self, event);
        };
    };
} else {
    const connectedWorkers = new WeakSet();
    const create = WebRenderTargetRegistry.create;
    WebRenderTargetRegistry.create = function (pthreadId, ...args) {
        if (pthreadId) {
            const thread = getDotnetRuntime(0).Module.PThread.pthreads[pthreadId];
            const worker = thread instanceof Worker ? thread : thread?.worker;
            if (!worker)
                throw new Error('The Avalonia render worker is unavailable.');
            if (!connectedWorkers.has(worker)) {
                worker.addEventListener('error', event =>
                    window.dispatchEvent(new CustomEvent('lumi-render-worker-error', { detail: event.message })));
                worker.addEventListener('message', ({ data }) => {
                    if (data?.lumiRenderWorker === 'wake' && Number.isFinite(data.delay))
                        TimerHelper.setTimeout(data.delay);
                    else if (data?.lumiRenderWorker === 'canvas')
                        window.dispatchEvent(new CustomEvent('lumi-render-worker-canvas', { detail: data.kind }));
                    else if (data?.lumiRenderWorker === 'context-lost')
                        window.dispatchEvent(new Event('lumi-render-worker-context-lost'));
                });
                connectedWorkers.add(worker);
            }
        }
        return create.call(this, pthreadId, ...args);
    };
}
