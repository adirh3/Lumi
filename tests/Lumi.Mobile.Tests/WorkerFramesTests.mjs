import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const source = name => readFileSync(
    new URL(`../../src/Lumi.Mobile.Browser/wwwroot/${name}`, import.meta.url), 'utf8');
const settle = async () => {
    for (let i = 0; i < 8; i++)
        await Promise.resolve();
};
const eventTarget = properties => {
    const listeners = new Map();
    return Object.assign(properties ?? {}, {
        addEventListener(type, callback) {
            listeners.set(type, [...(listeners.get(type) ?? []), callback]);
        },
        removeEventListener(type, callback) {
            listeners.set(type, (listeners.get(type) ?? []).filter(value => value !== callback));
        },
        dispatch(type, event = {}) {
            event.type = type;
            for (const callback of [...(listeners.get(type) ?? [])])
                callback(event);
        }
    });
};
const bitmap = (width = 100, height = 200) => ({
    width, height, closed: 0, close() { this.closed++; }
});

function host({ threaded = false, showStatus = true, unsupported = false, workerError, snapshot, snapshotError } = {}) {
    let now = 0, nextFrame = 0, nextTimer = 0;
    const frames = new Map(), timers = new Map(), errors = [], warnings = [], captures = [];
    const workers = [], children = [];
    class Canvas {
        constructor(app = false) {
            this.app = app;
            this.classList = { contains: name => app && name === 'avalonia-canvas' };
            this.style = { opacity: '' };
            this.dataset = {};
            this.parentElement = {};
            this.forwarded = [];
            this.context = {
                drawArrays() {}, drawElements() {}, clear() {},
                getParameter() {}
            };
        }
        closest(selector) { return this.app && selector === '#out' ? {} : null; }
        getContext(type, options) {
            this.options = options;
            this.contextType = type;
            return this.context;
        }
        transferControlToOffscreen() { return {}; }
        setAttribute() {}
        after(child) { this.presenter = child; children.push(child); }
        remove() { this.removed = true; }
        dispatchEvent(event) { this.forwarded.push(event); }
    }
    class FakeWorker {
        constructor() {
            if (workerError)
                throw workerError;
            this.messages = [];
            workers.push(this);
        }
        postMessage(message) { this.messages.push(message); }
        terminate() { this.terminated = true; }
    }
    class PointerEvent {
        constructor(type, properties) { Object.assign(this, properties, { type }); }
    }
    const window = eventTarget({
        requestAnimationFrame(callback) {
            const id = ++nextFrame;
            frames.set(id, callback);
            return id;
        },
        cancelAnimationFrame(id) { frames.delete(id); },
        reportError: error => errors.push(error)
    });
    const document = eventTarget({
        hidden: false,
        body: { appendChild: child => children.push(child) },
        createElement: type => type === 'canvas' ? new Canvas() : {
            dataset: {}, style: {}, setAttribute() {}
        }
    });
    const context = vm.createContext({
        window, document, HTMLCanvasElement: Canvas, PointerEvent,
        Worker: FakeWorker, OffscreenCanvas: unsupported ? undefined : class {},
        navigator: { userAgent: 'Chrome/143.0.0.0' },
        performance: { timeOrigin: 1000000, now: () => now },
        URL, queueMicrotask,
        createImageBitmap(canvas) {
            captures.push(canvas);
            if (snapshotError)
                throw snapshotError;
            return snapshot?.() ?? Promise.resolve(bitmap());
        },
        setTimeout(callback, ms) {
            const id = ++nextTimer;
            timers.set(id, { callback, ms });
            return id;
        },
        clearTimeout: id => timers.delete(id),
        console: { warn: (...args) => warnings.push(args) }
    });
    vm.runInContext(source('workerFrames.js')
        .replace(/^export /gm, '')
        .replace('import.meta.url', '"https://lumi.test/app/workerFrames.js"'), context);
    const nativeRaf = window.requestAnimationFrame;
    const originalGetContext = Canvas.prototype.getContext;
    const status = context.installWorkerFrames({ threaded, showStatus });
    return {
        window, document, context, status, frames, timers, workers, errors, warnings, captures,
        Canvas, children, nativeRaf, originalGetContext,
        canvas: () => new Canvas(true),
        time(time) { now = time; },
        native(time) {
            now = time;
            const pending = [...frames.values()];
            frames.clear();
            for (const callback of pending)
                callback(time);
        },
        tick(time, presented = 0) {
            now = time;
            workers[0].onmessage({ data: {
                vsync: 1000000 + time, sent: 1000000 + time, presented
            } });
        },
        pointer(type, target, { x = 20, y = 30, time = now, pointerId = 1, pointerType = 'touch' } = {}) {
            const event = {
                target, pointerId, pointerType, clientX: x, clientY: y,
                offsetX: x - 2, offsetY: y - 3, screenX: x + 10, screenY: y + 10,
                timeStamp: time, buttons: 1, isPrimary: true,
                stopImmediatePropagation() { this.stopped = true; }
            };
            window.dispatch(type, event);
            return event;
        }
    };
}

test('unsupported browsers and threaded builds keep the original renderer without patching APIs', () => {
    for (const options of [{ unsupported: true }, { threaded: true }]) {
        const app = host(options);
        assert.equal(app.status.state, 'fallback');
        assert.equal(app.window.requestAnimationFrame, app.nativeRaf);
        assert.equal(app.Canvas.prototype.getContext, app.originalGetContext);
        assert.equal(app.workers.length, 0);
        assert.equal(app.warnings.length, 1);
    }
});

test('normal rendering has no diagnostic badge', () => {
    const app = host({ showStatus: false });
    assert.equal(app.children.length, 0);
    assert.equal(app.status.state, 'starting');
});

test('only the Avalonia app canvas gets a worker presenter and retained drawing buffer', async () => {
    const app = host();
    const other = new app.Canvas();
    other.getContext('webgl2', { alpha: false });
    assert.equal(other.options.preserveDrawingBuffer, undefined);
    const canvas = app.canvas();
    const gl = canvas.getContext('webgl2', { alpha: false });
    assert.equal(canvas.options.preserveDrawingBuffer, true);
    assert.equal(canvas.options.alpha, false);
    assert.equal(app.workers[0].messages[0].type, 'init');
    assert.equal(canvas.style.opacity, '');
    gl.drawArrays();
    gl.drawElements();
    gl.clear();
    await settle();
    assert.equal(app.captures.length, 1);
    assert.equal(app.workers[0].messages.filter(value => value.type === 'frame').length, 1);
    app.tick(16, 1);
    assert.equal(canvas.style.opacity, '0');
    assert.equal(app.status.state, 'active');
    assert.equal(app.status.presented, 1);
});

test('worker ticks drive callbacks, honor same-frame cancellation and defer newly queued callbacks', async () => {
    const app = host();
    const gl = app.canvas().getContext('webgl2');
    gl.drawArrays();
    await settle();
    app.tick(10, 1);
    const ran = [];
    let cancelled;
    app.window.requestAnimationFrame(time => {
        ran.push(time);
        app.window.cancelAnimationFrame(cancelled);
        app.window.requestAnimationFrame(next => ran.push(next));
    });
    cancelled = app.window.requestAnimationFrame(() => ran.push('cancelled'));
    app.tick(20, 1);
    assert.deepEqual(ran, [20]);
    app.tick(28.333, 1);
    assert.equal(ran.length, 2);
    assert.ok(Math.abs(ran[1] - 28.333) < 0.000001);
    assert.equal(app.frames.size, 0);
});

test('the first presentation clears the starting badge without rewriting canvas styles each tick', async () => {
    const app = host();
    const canvas = app.canvas();
    canvas.getContext('webgl2').drawArrays();
    await settle();
    let writes = 0;
    Object.defineProperty(canvas.style, 'opacity', {
        configurable: true, get: () => '0', set() { writes++; }
    });
    app.tick(10, 1);
    assert.equal(app.children[0].textContent, 'Worker renderer');
    app.tick(20, 1);
    app.tick(30, 2);
    assert.equal(writes, 1);
});

test('a 120-Hz worker clock can drive continuously drawing callbacks without native RAF throttling', async () => {
    const app = host();
    const gl = app.canvas().getContext('webgl2');
    gl.drawArrays();
    await settle();
    app.tick(10, 1);
    let rendered = 0;
    function draw() {
        assert.equal(this, app.window);
        rendered++;
        gl.drawArrays();
        app.window.requestAnimationFrame(draw);
    }
    app.window.requestAnimationFrame(draw);
    for (let i = 1; i <= 30; i++) {
        app.tick(10 + i * 1000 / 120, i + 1);
        await settle();
    }
    assert.equal(rendered, 30);
    assert.equal(app.status.clockHz, 120);
    assert.equal(app.frames.size, 0);
});

test('the diagnostic badge distinguishes new presented frames from the worker clock', async () => {
    const app = host();
    const gl = app.canvas().getContext('webgl2');
    gl.drawArrays();
    await settle();
    app.tick(10, 1);
    for (let i = 1; i <= 240; i++) {
        app.tick(10 + i * 1000 / 120, 1 + Math.floor(i / 2));
        if (i % 2 === 0)
            gl.drawArrays();
        await settle();
    }
    assert.equal(app.status.clockHz, 120);
    assert.match(app.children[0].textContent, /120 Hz clock, 60 new frames\/s/);
});

test('a delayed worker tick uses the current frame for input and does not discard the only wake-up', async () => {
    const app = host();
    const canvas = app.canvas();
    canvas.getContext('webgl2').drawArrays();
    await settle();
    app.tick(10, 1);
    app.pointer('pointerdown', canvas, { x: 10, time: 10 });
    app.pointer('pointermove', canvas, { x: 30, time: 30 });
    app.pointer('pointermove', canvas, { x: 90, time: 90 });
    const ran = [];
    app.window.requestAnimationFrame(time => ran.push(time));
    app.time(100);
    app.workers[0].onmessage({ data: {
        vsync: 1000020, sent: 1000020, interval: 1000 / 120, presented: 1
    } });
    assert.equal(ran.length, 1);
    assert.ok(ran[0] >= 90);
    assert.equal(canvas.forwarded.length, 1);
    assert.ok(canvas.forwarded[0].clientX >= 80);
    await settle();
    assert.equal(app.workers[0].messages.at(-1).type, 'tick-done');
});

test('slow input delivery does not leave animation callbacks using an old frame time', async () => {
    const app = host();
    const canvas = app.canvas();
    canvas.getContext('webgl2').drawArrays();
    await settle();
    app.tick(10, 1);
    app.pointer('pointerdown', canvas, { x: 10, time: 10 });
    app.pointer('pointermove', canvas, { x: 30, time: 30 });
    const dispatch = canvas.dispatchEvent;
    canvas.dispatchEvent = function (event) {
        dispatch.call(this, event);
        app.time(130);
    };
    const ran = [];
    app.window.requestAnimationFrame(time => ran.push(time));
    app.tick(30, 1);
    assert.equal(ran.length, 1);
    assert.ok(ran[0] >= 120);
});

test('the clock slot is released before animation callbacks render the next frame', async () => {
    const app = host();
    app.canvas().getContext('webgl2').drawArrays();
    await settle();
    app.tick(10, 1);
    let ran = false;
    app.window.requestAnimationFrame(() => {
        assert.equal(app.workers[0].messages.at(-1).type, 'tick-done');
        ran = true;
    });
    app.tick(20, 1);
    assert.equal(ran, true);
});

test('coalesced main-thread messages retain the worker refresh rate instead of reporting callback throughput', async () => {
    const app = host();
    const canvas = app.canvas();
    const gl = canvas.getContext('webgl2');
    for (let i = 1; i <= 30; i++) {
        const time = i * 1000 / 60;
        app.time(time);
        gl.drawArrays();
        await settle();
        app.workers[0].onmessage({ data: {
            vsync: 1000000 + time, sent: 1000000 + time,
            interval: 1000 / 120, presented: i
        } });
    }
    assert.equal(app.status.clockHz, 120);
});

test('a failed tick acknowledgement restores native rendering and reports the error', async () => {
    const app = host();
    const canvas = app.canvas();
    canvas.getContext('webgl2').drawArrays();
    await settle();
    const worker = app.workers[0];
    const postMessage = worker.postMessage;
    worker.postMessage = function (message) {
        if (message.type === 'tick-done')
            throw new Error('The worker channel closed');
        postMessage.call(this, message);
    };
    app.tick(10, 1);
    await settle();
    assert.equal(app.status.state, 'fallback');
    assert.match(app.status.error, /worker channel closed/);
    assert.equal(canvas.style.opacity, '');
    assert.equal(worker.terminated, true);
    assert.equal(app.warnings.length, 1);
});

test('callback errors are reported and frame times cannot go backwards across worker/fallback clocks', async () => {
    const app = host();
    app.canvas().getContext('webgl2').drawArrays();
    await settle();
    app.tick(20, 1);
    const times = [];
    app.window.requestAnimationFrame(() => { throw new Error('callback failed'); });
    app.window.requestAnimationFrame(time => times.push(time));
    app.tick(30, 1);
    app.window.requestAnimationFrame(time => times.push(time));
    app.tick(29, 1);
    assert.equal(app.errors.length, 1);
    assert.ok(times[1] > times[0]);
    app.workers[0].onerror({ message: 'Worker fixture crash' });
    app.window.requestAnimationFrame(time => times.push(time));
    app.native(28);
    assert.ok(times[2] > times[1]);
});

test('worker failure restores the original canvas, WebGL methods, native scheduling and pending callbacks', async () => {
    const app = host();
    const canvas = app.canvas();
    const draw = canvas.context.drawArrays;
    canvas.getContext('webgl2').drawArrays();
    await settle();
    app.tick(10, 1);
    let called = 0;
    app.window.requestAnimationFrame(() => called++);
    app.workers[0].onerror({ message: 'Fixture stopped' });
    app.native(20);
    assert.equal(called, 1);
    assert.equal(app.status.state, 'fallback');
    assert.equal(canvas.style.opacity, '');
    assert.equal(canvas.presenter.removed, true);
    assert.equal(canvas.context.drawArrays, draw);
    assert.equal(app.Canvas.prototype.getContext, app.originalGetContext);
    assert.equal(app.workers[0].terminated, true);
    assert.equal(app.warnings.length, 1);
});

test('snapshot failures surface a fallback and a late snapshot is closed after worker failure', async () => {
    const broken = host({ snapshotError: new Error('GPU snapshot failed') });
    broken.canvas().getContext('webgl2').drawArrays();
    await settle();
    assert.equal(broken.status.state, 'fallback');
    assert.match(broken.status.error, /GPU snapshot/);
    let finish;
    const app = host({ snapshot: () => new Promise(resolve => { finish = resolve; }) });
    app.canvas().getContext('webgl2').drawArrays();
    await settle();
    app.workers[0].onerror({ message: 'Fixture stopped' });
    const frame = bitmap();
    finish(frame);
    await settle();
    assert.equal(frame.closed, 1);
});

test('snapshot backpressure keeps one capture in flight and captures a dirty successor', async () => {
    const pending = [];
    const app = host({ snapshot: () => new Promise(resolve => pending.push(resolve)) });
    const gl = app.canvas().getContext('webgl2');
    gl.drawArrays();
    await settle();
    for (let i = 0; i < 20; i++) {
        gl.drawArrays();
        await settle();
    }
    assert.equal(app.captures.length, 1);
    pending[0](bitmap());
    await settle();
    assert.equal(app.captures.length, 2);
    pending[1](bitmap());
    await settle();
    assert.equal(app.captures.length, 2);
});

test('idle drawing returns to native scheduling, and a new draw wakes the worker clock', async () => {
    const app = host();
    const gl = app.canvas().getContext('webgl2');
    gl.drawArrays();
    await settle();
    app.tick(10, 1);
    app.window.requestAnimationFrame(() => {});
    app.tick(200, 1);
    assert.equal(app.status.clockActive, false);
    assert.equal(app.workers[0].messages.at(-1).value, false);
    assert.equal(app.frames.size, 1);
    app.time(210);
    gl.drawArrays();
    await settle();
    assert.equal(app.status.clockActive, true);
    assert.equal(app.frames.size, 0);
});

test('backgrounding stops worker-driven callbacks/captures and foregrounding resumes them', async () => {
    const app = host();
    const gl = app.canvas().getContext('webgl2');
    gl.drawArrays();
    await settle();
    app.tick(10, 1);
    app.document.hidden = true;
    app.document.dispatch('visibilitychange');
    gl.drawArrays();
    await settle();
    assert.equal(app.status.clockActive, false);
    assert.equal(app.captures.length, 1);
    app.document.hidden = false;
    app.document.dispatch('visibilitychange');
    await settle();
    assert.equal(app.status.clockActive, true);
    assert.equal(app.captures.length, 2);
});

test('touch resampling preserves offsets and multiple pointers; native editors, mouse and pen are untouched', async () => {
    const app = host();
    const canvas = app.canvas();
    canvas.getContext('webgl2').drawArrays();
    await settle();
    app.tick(10, 1);
    for (const pointerType of ['mouse', 'pen']) {
        app.pointer('pointerdown', canvas, { pointerType });
        assert.equal(app.pointer('pointermove', canvas, { pointerType }).stopped, undefined);
    }
    const editor = {};
    app.pointer('pointerdown', editor);
    assert.equal(app.pointer('pointermove', editor).stopped, undefined);
    app.pointer('pointerdown', canvas, { x: 10, time: 10 });
    app.pointer('pointermove', canvas, { x: 30, time: 30 });
    app.pointer('pointerdown', canvas, { pointerId: 2, x: 40, time: 10 });
    app.pointer('pointermove', canvas, { pointerId: 2, x: 60, time: 30 });
    app.tick(32, 1);
    assert.equal(canvas.forwarded.length, 2);
    assert.equal(canvas.forwarded[0].clientX, 20);
    assert.equal(canvas.forwarded[0].offsetX, 18);
    assert.equal(canvas.forwarded[1].clientX, 50);
    assert.equal(canvas.forwarded[1].pointerId, 2);
    app.pointer('pointerup', canvas, { x: 35, time: 35 });
    assert.equal(canvas.forwarded.at(-1).clientX, 35);
    assert.equal(canvas.forwarded.at(-1).timeStamp, 35);
    app.pointer('pointercancel', canvas, { pointerId: 2, time: 36 });
    const count = canvas.forwarded.length;
    app.tick(40, 1);
    assert.equal(canvas.forwarded.length, count);
});

test('pointer interpolation and bounded prediction do not keep extrapolating after a finger stops', () => {
    const app = host();
    const samples = [{ time: 0, x: 0, y: 5 }, { time: 10, x: 10, y: 15 }];
    assert.equal(app.context.samplePointerPosition(samples, 5).x, 5);
    assert.equal(app.context.samplePointerPosition(samples, 16).x, 16);
    assert.equal(app.context.samplePointerPosition(samples, 22).x, 18);
    assert.equal(app.context.samplePointerPosition(samples, 23).x, 10);
});

test('the empty Avalonia native host receives resampling without intercepting its native child controls', async () => {
    const app = host();
    const canvas = app.canvas();
    canvas.getContext('webgl2').drawArrays();
    await settle();
    app.tick(10, 1);
    const nativeHost = {
        parentElement: canvas.parentElement,
        classList: { contains: name => name === 'avalonia-native-host' },
        moves: [], dispatchEvent(event) { this.moves.push(event); }
    };
    app.pointer('pointerdown', nativeHost, { x: 10, time: 10 });
    assert.equal(app.pointer('pointermove', nativeHost, { x: 30, time: 30 }).stopped, true);
    app.tick(32, 1);
    assert.equal(nativeHost.moves[0].clientX, 20);
    const nativeChild = { parentElement: nativeHost, classList: { contains: () => false } };
    app.pointer('pointerdown', nativeChild, { pointerId: 2, x: 10, time: 30 });
    assert.equal(app.pointer('pointermove', nativeChild, { pointerId: 2, x: 30, time: 35 }).stopped, undefined);
});

test('worker construction and startup failures are explicit and preserve native rendering', () => {
    const failed = host({ workerError: new Error('Worker blocked') });
    assert.equal(failed.status.state, 'fallback');
    let called = false;
    failed.window.requestAnimationFrame(() => { called = true; });
    failed.native(10);
    assert.equal(called, true);
    const timeout = host();
    const canvas = timeout.canvas();
    canvas.getContext('webgl2');
    [...timeout.timers.values()][0].callback();
    assert.equal(timeout.status.state, 'fallback');
    assert.equal(canvas.style.opacity, '');
});

test('a worker clock that stalls after startup falls back without leaving the app frozen', async () => {
    const app = host();
    const canvas = app.canvas();
    canvas.getContext('webgl2').drawArrays();
    await settle();
    app.tick(10, 1);
    const deadline = [...app.timers.values()].find(timer => timer.ms === 5000);
    assert.ok(deadline);
    let called = false;
    app.window.requestAnimationFrame(() => { called = true; });
    app.time(5011);
    deadline.callback();
    assert.equal(app.status.state, 'fallback');
    assert.match(app.status.error, /clock stopped responding/);
    app.native(5020);
    assert.equal(called, true);
    assert.equal(canvas.style.opacity, '');
});

function presenter({ unsupported = false, contextError = false } = {}) {
    const frames = [], messages = [], shown = [];
    const canvas = {
        width: 1, height: 1,
        getContext(type) {
            assert.equal(type, 'bitmaprenderer');
            return contextError ? null : { transferFromImageBitmap: frame => shown.push(frame) };
        }
    };
    const self = {};
    const context = vm.createContext({
        self,
        requestAnimationFrame: unsupported ? undefined : callback => frames.push(callback),
        performance: { timeOrigin: 1000, now: () => 100 },
        postMessage: value => messages.push(value)
    });
    vm.runInContext(source('workerFramePresenter.js'), context);
    return {
        frames, messages, shown, canvas,
        send: data => self.onmessage({ data }),
        tick: time => frames.shift()(time)
    };
}

test('worker presents real transferred frames, resizes, and bounds pending image ownership', () => {
    const app = presenter();
    app.send({ type: 'init', canvas: app.canvas });
    app.send({ type: 'wanted', value: true });
    const old = bitmap(), latest = bitmap(300, 400);
    app.send({ type: 'frame', frame: old });
    app.send({ type: 'frame', frame: latest });
    assert.equal(old.closed, 1);
    assert.equal(app.frames.length, 1);
    app.tick(8);
    assert.deepEqual(app.shown, [latest]);
    assert.equal(app.canvas.width, 300);
    assert.equal(app.canvas.height, 400);
    assert.equal(latest.closed, 1);
    assert.equal(app.messages.at(-1).presented, 1);
    assert.equal(app.messages.at(-1).vsync, 1008);
    app.send({ type: 'wanted', value: false });
    app.tick(16);
    assert.equal(app.frames.length, 0);
});

test('a busy main thread cannot accumulate worker ticks, while bitmap presentation continues', () => {
    const app = presenter();
    app.send({ type: 'init', canvas: app.canvas });
    app.send({ type: 'wanted', value: true });
    app.tick(8);
    for (const time of [16, 24, 32])
        app.tick(time);
    assert.equal(app.messages.length, 1);
    const frame = bitmap();
    app.send({ type: 'frame', frame });
    app.tick(40);
    assert.deepEqual(app.shown, [frame]);
    assert.equal(frame.closed, 1);
    assert.equal(app.messages.length, 1);
    app.send({ type: 'tick-done' });
    app.tick(48);
    assert.equal(app.messages.length, 2);
    assert.equal(app.messages.at(-1).vsync, 1048);
    assert.equal(app.messages.at(-1).interval, 8);
    assert.equal(app.messages.at(-1).presented, 1);
});

test('an idle worker reports a frame presented during a pending tick and then sleeps', () => {
    const app = presenter();
    app.send({ type: 'init', canvas: app.canvas });
    app.tick(8);
    app.send({ type: 'frame', frame: bitmap() });
    app.tick(16);
    assert.equal(app.messages.length, 1);
    assert.equal(app.frames.length, 0);
    app.send({ type: 'tick-done' });
    assert.equal(app.frames.length, 1);
    app.tick(24);
    assert.equal(app.messages.length, 2);
    assert.equal(app.messages.at(-1).presented, 1);
    app.send({ type: 'tick-done' });
    assert.equal(app.frames.length, 0);
});

test('worker reports unavailable APIs and closes frames that cannot be presented', () => {
    for (const options of [{ unsupported: true }, { contextError: true }]) {
        const app = presenter(options);
        app.send({ type: 'init', canvas: app.canvas });
        assert.ok(app.messages.at(-1).error);
        assert.equal(app.frames.length, 0);
    }
    const uninitialized = presenter();
    const frame = bitmap();
    uninitialized.send({ type: 'frame', frame });
    assert.equal(frame.closed, 1);
    assert.match(uninitialized.messages[0].error, /initialized/);
});

test('the renderer defaults to Android, permits explicit testing and retains its MIT attribution', () => {
    const main = source('main.js');
    assert.match(main, /workerFrameMode === '1' \|\| workerFrameMode !== '0' &&\s*\/Android\//);
    assert.ok(main.indexOf("await import('./workerFrames.js')") < main.indexOf('await runtime.runMain'));
    for (const name of ['workerFrames.js', 'workerFramePresenter.js']) {
        assert.match(source(name), /Copyright \(c\) 2026 Mathiew Abbas/);
        assert.match(source(name), /Permission is hereby granted/);
    }
});
