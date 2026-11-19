// Preview-only entry point: stage beside main.js, not in the shipped app shell.
const clock = () => performance.now();
const frame = window.requestAnimationFrame.bind(window);
const round = value => Math.round(value * 10) / 10;
const out = document.getElementById('out');
const attempts = [];
const reports = [];
let surface, armed, capture, lastPresented = 0, contextLosses = 0;

const panel = document.createElement('section');
panel.id = 'lumi-phone-perf';
panel.hidden = true;
panel.setAttribute('aria-label', 'Performance diagnostics');
panel.innerHTML = `<strong>Performance diagnostics</strong>
  <p data-backend>Waiting for Lumi's canvas...</p>
  <p>Pick a recording, then do that action once in the app.</p>
  <div class="perf-controls">
    <button type="button" data-action="Title menu">Title menu</button>
    <button type="button" data-action="Model picker">Model picker</button>
    <button type="button" data-action="Scroll">Scroll</button>
  </div>
  <p data-result role="status"></p>
  <textarea readonly aria-label="Diagnostic report" hidden></textarea>
  <div class="perf-controls">
    <button type="button" data-copy>Copy report</button>
    <button type="button" data-close>Close</button>
  </div>`;
const chip = document.createElement('button');
chip.id = 'lumi-perf-button';
chip.type = 'button';
chip.textContent = 'Perf';
chip.setAttribute('aria-controls', panel.id);
chip.setAttribute('aria-expanded', 'false');
const indicator = document.createElement('output');
indicator.id = 'lumi-perf-indicator';
indicator.hidden = true;
const style = document.createElement('style');
style.textContent = `
  #lumi-perf-button, #lumi-perf-indicator, #lumi-phone-perf {
    position: fixed; z-index: 9999; box-sizing: border-box;
    color: #eef4fa; background: #19212c; border: 1px solid #526478;
    font: 14px/1.35 system-ui; border-radius: 12px;
  }
  #lumi-perf-button { right: calc(env(safe-area-inset-right, 0px) + 8px);
    bottom: calc(env(safe-area-inset-bottom, 0px) + 92px); min-width: 56px; min-height: 48px; }
  #lumi-perf-indicator { top: calc(env(safe-area-inset-top, 0px) + 4px); left: 50%;
    transform: translateX(-50%); pointer-events: none; padding: 4px 8px; white-space: nowrap; }
  #lumi-phone-perf { left: max(8px, env(safe-area-inset-left)); right: max(8px, env(safe-area-inset-right));
    bottom: calc(env(safe-area-inset-bottom, 0px) + 8px); max-width: 500px; margin: auto;
    padding: 12px; max-height: 72dvh; overflow: auto; overscroll-behavior: contain; }
  #lumi-phone-perf p { margin: 8px 0; overflow-wrap: anywhere; }
  #lumi-phone-perf .perf-controls { display: flex; flex-wrap: wrap; gap: 6px; }
  #lumi-phone-perf button { min-height: 48px; padding: 6px 12px; color: inherit; background: #2b3a4b;
    border: 1px solid #526478; border-radius: 8px; font: inherit; }
  #lumi-phone-perf button:disabled { opacity: .5; }
  #lumi-phone-perf textarea { box-sizing: border-box; width: 100%; height: 7em;
    color: inherit; background: #111821; margin-bottom: 8px; font: 12px monospace; }
  #lumi-phone-perf[hidden], #lumi-perf-button[hidden], #lumi-perf-indicator[hidden],
  #lumi-phone-perf [hidden] { display: none !important; }
`;
document.head.append(style);
document.body.append(chip, indicator, panel);

const output = panel.querySelector('textarea');
const message = panel.querySelector('[data-result]');
const actions = [...panel.querySelectorAll('[data-action]')];
for (const button of actions) button.disabled = true;

const nativeGetContext = HTMLCanvasElement.prototype.getContext;
HTMLCanvasElement.prototype.getContext = function (type, ...args) {
    const context = nativeGetContext.call(this, type, ...args);
    if (!this.classList.contains('avalonia-canvas') || !out.contains(this))
        return context;

    if (attempts.length < 12 && !attempts.some(row => row.type === type && row.ok === Boolean(context)))
        attempts.push({ type, ok: Boolean(context) });
    if (!context || surface?.context === context)
        return context;

    const gl = type === 'webgl2' || type === 'webgl';
    const extension = gl ? context.getExtension('WEBGL_debug_renderer_info') : null;
    surface = {
        canvas: this, context, type,
        renderer: extension ? context.getParameter(extension.UNMASKED_RENDERER_WEBGL) : null,
        maskedRenderer: gl ? context.getParameter(context.RENDERER) : null,
        preserveDrawingBuffer: gl ? context.getContextAttributes()?.preserveDrawingBuffer : null
    };
    this.addEventListener('webglcontextlost', () => contextLosses++);
    panel.querySelector('[data-backend]').textContent = `${type === '2d' ? 'Software 2D' : type.toUpperCase()}`
        + ` · ${surface.renderer ?? surface.maskedRenderer ?? 'renderer not exposed'}`;
    for (const button of actions) button.disabled = false;
    return context;
};

const NativeWorker = globalThis.Worker;
if (typeof NativeWorker === 'function') globalThis.Worker = class extends NativeWorker {
    constructor(url, options) {
        super(url, options);
        if (!new URL(url, location.href).pathname.endsWith('/workerFramePresenter.js'))
            return;
        this.addEventListener('message', ({ data }) => {
            if (!Number.isFinite(data?.vsync) || !Number.isInteger(data.presented))
                return;
            const count = data.presented - lastPresented;
            lastPresented = data.presented;
            if (capture && count > 0) {
                const stamp = data.vsync - performance.timeOrigin;
                if (stamp >= capture.start && capture.presented.length < 1200)
                    capture.presented.push({ stamp, count, deliveryMs: round(clock() - stamp) });
            }
        });
    }
};

function installDrawProbe(sample) {
    const { context, type } = surface;
    const methods = type === '2d' ? ['putImageData', 'drawImage'] :
        ['clear', 'clearBufferfv', 'drawArrays', 'drawElements', 'drawArraysInstanced',
            'drawElementsInstanced', 'drawRangeElements', 'blitFramebuffer'];
    const restore = [];
    let queued = false;
    for (const name of methods) {
        const original = context[name];
        if (typeof original !== 'function') continue;
        const instrumented = function (...args) {
            const result = original.apply(this, args);
            if (!queued) {
                queued = true;
                queueMicrotask(() => {
                    queued = false;
                    if (capture === sample && sample.draws.length < 1200) sample.draws.push(clock());
                });
            }
            return result;
        };
        context[name] = instrumented;
        restore.push(() => { if (context[name] === instrumented) context[name] = original; });
    }
    return () => restore.forEach(undo => undo());
}

function series(stamps, start, end) {
    const times = stamps.filter(time => time >= start && time <= end);
    const gaps = times.slice(1).map((time, index) => time - times[index]).sort((a, b) => a - b);
    const percentile = fraction => gaps.length ? round(gaps[Math.ceil((gaps.length - 1) * fraction)]) : null;
    return {
        count: times.length, firstMs: times.length ? round(times[0] - start) : null,
        gapP50Ms: percentile(.5), gapP95Ms: percentile(.95), gapMaxMs: percentile(1)
    };
}

function reportText() {
    const runtime = globalThis.getDotnetRuntime?.(0);
    const info = runtime?.runtimeBuildInfo;
    const graphics = surface && {
        type: surface.type, attempts, contextLosses,
        renderer: surface.renderer, maskedRenderer: surface.maskedRenderer,
        preserveDrawingBuffer: surface.preserveDrawingBuffer,
        canvasPixels: [surface.canvas.width, surface.canvas.height],
        viewportCss: [innerWidth, innerHeight], dpr: devicePixelRatio
    };
    const worker = globalThis.lumiWorkerFrames;
    return JSON.stringify({
        diagnostic: 'Lumi phone performance v1',
        browser: navigator.userAgent.match(/Chrome\/[\d.]+|Firefox\/[\d.]+/)?.[0] ?? null,
        runtime: info && { version: info.productVersion, configuration: info.buildConfiguration,
            threads: info.wasmEnableThreads, simd: info.wasmEnableSIMD,
            nativeExceptions: info.wasmEnableExceptionHandling,
            wasmMemoryMiB: runtime.Module?.HEAPU8 ? round(runtime.Module.HEAPU8.byteLength / 1048576) : null },
        graphics,
        presentation: { worker: worker?.state ?? 'not requested', workerClockHz: worker?.clockHz ?? null },
        captures: reports
    }, null, 2);
}

function showPanel(show) {
    panel.hidden = !show;
    chip.setAttribute('aria-expanded', String(show));
    if (show) output.value = reportText();
}

chip.addEventListener('click', () => showPanel(panel.hidden));
panel.querySelector('[data-close]').addEventListener('click', () => showPanel(false));
panel.querySelector('[data-copy]').addEventListener('click', async () => {
    output.value = reportText();
    try {
        await navigator.clipboard.writeText(output.value);
        message.textContent = 'Copied. Paste the report into our chat.';
    } catch {
        output.hidden = false;
        output.focus();
        output.select();
        message.textContent = 'Clipboard unavailable. Select and copy the report below.';
    }
});

for (const button of actions) button.addEventListener('click', () => {
    if (capture) return;
    const action = button.dataset.action;
    armed = { action };
    const ticket = armed;
    showPanel(false);
    indicator.textContent = action === 'Scroll' ? 'Armed · scroll the chat' : `Armed · open ${action.toLowerCase()}`;
    indicator.hidden = false;
    setTimeout(() => {
        if (armed !== ticket) return;
        armed = null;
        indicator.hidden = true;
        message.textContent = 'No app touch was recorded. Try again.';
        showPanel(true);
    }, 10000);
});

window.addEventListener('pointerdown', event => {
    if (!armed || !surface || !event.isTrusted || !(event.target instanceof Node) ||
        !out.contains(event.target) || document.hidden)
        return;
    const action = armed.action;
    armed = null;
    const start = clock();
    const duration = action === 'Scroll' ? 2500 : 1000;
    const sample = capture = {
        start, action, pointer: event.pointerId, release: null,
        draws: [], raf: [], presented: [], visible: true
    };
    chip.hidden = indicator.hidden = true;
    const restore = installDrawProbe(sample);
    const supportsLongTasks = globalThis.PerformanceObserver?.supportedEntryTypes?.includes('longtask');
    let observer, longTasks = [];
    const addTasks = entries => {
        for (const task of entries) if (longTasks.length < 50)
            longTasks.push({ start: task.startTime, duration: task.duration });
    };
    if (supportsLongTasks) {
        observer = new PerformanceObserver(list => addTasks(list.getEntries()));
        observer.observe({ type: 'longtask' });
    }
    const tick = time => {
        if (capture !== sample) return;
        if (sample.raf.length < 1200) sample.raf.push(time);
        frame(tick);
    };
    frame(tick);
    setTimeout(() => {
        capture = null;
        restore();
        chip.hidden = false;
        if (observer) {
            addTasks(observer.takeRecords());
            observer.disconnect();
        }
        if (!sample.visible) {
            message.textContent = 'Recording discarded: the preview went into the background.';
            showPanel(true);
            return;
        }
        const from = action === 'Scroll' ? start : sample.release ?? start;
        const to = Math.min(start + duration, from + (action === 'Scroll' ? 2500 : 500));
        const updates = sample.presented.filter(item => item.stamp >= from && item.stamp <= to);
        reports.push({
            action, measuredFrom: from === start ? 'pointer down' : 'pointer up',
            windowMs: round(to - from),
            browserRaf: series(sample.raf, from, to),
            renderBatches: series(sample.draws, from, to),
            workerUpdates: series(updates.map(item => item.stamp), from, to),
            workerPresented: updates.reduce((total, item) => total + item.count, 0),
            workerDeliveryMaxMs: updates.length ? Math.max(...updates.map(item => item.deliveryMs)) : null,
            longTasks: supportsLongTasks ? longTasks.filter(t => t.start <= to && t.start + t.duration >= from)
                .map(t => ({ atMs: round(t.start - from), durationMs: round(t.duration) })) : null
        });
        if (reports.length > 10) reports.shift();
        chip.textContent = `Perf (${reports.length})`;
        message.textContent = `${action} recorded. Open Perf again to copy or record another action.`;
        output.value = reportText();
    }, duration);
}, { capture: true, passive: true });

window.addEventListener('pointerup', event => {
    if (event.isTrusted && event.pointerId === capture?.pointer) capture.release = clock();
}, { capture: true, passive: true });
document.addEventListener('visibilitychange', () => {
    if (document.hidden && capture) capture.visible = false;
});

await import('./main.js');
