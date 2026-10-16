// Adapted from https://github.com/aumb/flutter_shim (clock and pointer resampling).
// MIT License
// Copyright (c) 2026 Mathiew Abbas
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

export function samplePointerPosition(samples, time) {
    const last = samples.at(-1);
    if (time >= last.time) {
        if (time - last.time > 12 || samples.length < 2)
            return last;
        let index = samples.length - 2;
        while (index > 0 && last.time - samples[index].time < 4)
            index--;
        const from = samples[index];
        const span = last.time - from.time;
        const ahead = Math.min(time - last.time, 8);
        return span > 0 ? {
            x: last.x + (last.x - from.x) / span * ahead,
            y: last.y + (last.y - from.y) / span * ahead
        } : last;
    }
    let index = samples.length - 1;
    while (index > 0 && samples[index - 1].time > time)
        index--;
    if (!index)
        return samples[0];
    const a = samples[index - 1], b = samples[index];
    const amount = (time - a.time) / (b.time - a.time || 1);
    return { x: a.x + (b.x - a.x) * amount, y: a.y + (b.y - a.y) * amount };
}

export function installWorkerFrames({ threaded = false, showStatus = false } = {}) {
    const status = {
        state: 'starting', clockActive: false, clockHz: 0,
        captured: 0, presented: 0, resampledMoves: 0, error: null
    };
    window.lumiWorkerFrames = status;
    let badge;
    if (showStatus) {
        badge = document.createElement('div');
        badge.dataset.lumiWorkerFrames = '';
        badge.setAttribute('role', 'status');
        badge.style.cssText = 'position:fixed;top:calc(env(safe-area-inset-top,0px) + 2px);' +
            'left:50%;transform:translateX(-50%);z-index:10000;pointer-events:none;' +
            'border-radius:8px;padding:2px 6px;background:#102b30e8;color:#b8f2e6;' +
            'font:10px system-ui;white-space:nowrap';
        badge.textContent = 'Worker renderer starting';
        document.body.appendChild(badge);
    }

    const supported = !threaded && /Chrome\//.test(navigator.userAgent) &&
        typeof Worker === 'function' && typeof OffscreenCanvas === 'function' &&
        typeof createImageBitmap === 'function' &&
        typeof HTMLCanvasElement.prototype.transferControlToOffscreen === 'function';
    if (!supported) {
        status.state = 'fallback';
        status.error = threaded ? 'This experiment requires the single-threaded build.' :
            'Worker canvas presentation is unavailable in this browser.';
        if (badge)
            badge.textContent = 'Normal renderer (worker unavailable)';
        console.warn('[Lumi PWA] ' + status.error);
        return status;
    }

    const nativeRaf = window.requestAnimationFrame.bind(window);
    const nativeCaf = window.cancelAnimationFrame.bind(window);
    const originalGetContext = HTMLCanvasElement.prototype.getContext;
    const callbacks = new Map();
    const touches = new Map();
    const periods = [];
    const listeners = [];
    let nextId = -1, nativeFrame = null, worker, surface;
    let failed = false, workerReady = false, workerWanted = false;
    let lastTick = 0, lastFrameTime = -Infinity, lastDraw = -Infinity;
    let period = 1000 / 60, skippedLast = false, dispatching = false;
    let startupTimeout, clockTimeout, lastMessage = 0, lastBadge = 0;

    const listen = (target, type, callback, options) => {
        target.addEventListener(type, callback, options);
        listeners.push(() => target.removeEventListener(type, callback, options));
    };
    const checkClock = () => {
        clockTimeout = undefined;
        if (failed || !status.clockActive || document.hidden)
            return;
        const remaining = 5000 - (performance.now() - lastMessage);
        if (remaining <= 0)
            fallback(new Error('The worker canvas clock stopped responding.'));
        else
            clockTimeout = setTimeout(checkClock, remaining);
    };
    const setWanted = wanted => {
        if (failed || !workerReady || workerWanted === wanted)
            return;
        workerWanted = wanted;
        clearTimeout(clockTimeout);
        clockTimeout = undefined;
        if (wanted) {
            lastMessage = performance.now();
            clockTimeout = setTimeout(checkClock, 5000);
        }
        try {
            worker.postMessage({ type: 'wanted', value: wanted });
        } catch (error) {
            fallback(error);
        }
    };
    const runCallbacks = time => {
        time = Math.max(time, lastFrameTime + 0.001);
        lastFrameTime = time;
        for (const [id, callback] of [...callbacks]) {
            if (!callbacks.delete(id))
                continue;
            try {
                callback.call(window, time);
            } catch (error) {
                window.reportError(error);
            }
        }
    };
    const schedule = () => {
        status.clockActive = !failed && workerReady && !document.hidden &&
            (touches.size > 0 || performance.now() - lastDraw < 160);
        setWanted(status.clockActive);
        if (status.clockActive) {
            if (nativeFrame !== null)
                nativeCaf(nativeFrame);
            nativeFrame = null;
        } else if (callbacks.size && nativeFrame === null) {
            nativeFrame = nativeRaf(time => {
                nativeFrame = null;
                runCallbacks(time);
                schedule();
            });
        }
    };
    window.requestAnimationFrame = callback => {
        if (typeof callback !== 'function')
            throw new TypeError('requestAnimationFrame requires a callback.');
        const id = nextId--;
        callbacks.set(id, callback);
        schedule();
        return id;
    };
    window.cancelAnimationFrame = id => {
        if (id < 0)
            callbacks.delete(id);
        else
            nativeCaf(id);
    };

    const dispatchMove = (touch, x, y, time) => {
        const event = touch.event;
        const move = new PointerEvent('pointermove', {
            bubbles: true, cancelable: true, composed: true,
            pointerId: event.pointerId, pointerType: event.pointerType, isPrimary: event.isPrimary,
            clientX: x, clientY: y,
            screenX: event.screenX + x - event.clientX,
            screenY: event.screenY + y - event.clientY,
            button: -1, buttons: event.buttons, pressure: event.pressure,
            width: event.width, height: event.height,
            tiltX: event.tiltX, tiltY: event.tiltY, twist: event.twist,
            altKey: event.altKey, ctrlKey: event.ctrlKey,
            shiftKey: event.shiftKey, metaKey: event.metaKey
        });
        Object.defineProperties(move, {
            timeStamp: { value: Math.max(time, touch.lastTime) },
            offsetX: { value: x + touch.offsetX },
            offsetY: { value: y + touch.offsetY }
        });
        touch.x = x;
        touch.y = y;
        touch.lastTime = move.timeStamp;
        dispatching = true;
        try {
            touch.target.dispatchEvent(move);
            status.resampledMoves++;
        } finally {
            dispatching = false;
        }
    };
    const flushTouch = touch => {
        const last = touch.samples.at(-1);
        if (last.x !== touch.x || last.y !== touch.y)
            dispatchMove(touch, last.x, last.y, last.time);
    };
    const fallback = error => {
        if (failed)
            return;
        failed = true;
        clearTimeout(startupTimeout);
        clearTimeout(clockTimeout);
        status.state = 'fallback';
        status.error = String(error?.message || error);
        if (badge)
            badge.textContent = 'Normal renderer (worker stopped)';
        console.warn('[Lumi PWA] Worker renderer stopped; using normal rendering.', error);
        worker?.terminate();
        for (const remove of listeners)
            remove();
        for (const touch of touches.values())
            flushTouch(touch);
        touches.clear();
        if (surface) {
            surface.canvas.style.opacity = surface.opacity;
            surface.presenter.remove();
            for (const [name, original] of surface.originals)
                surface.context[name] = original;
        }
        if (HTMLCanvasElement.prototype.getContext === getContext)
            HTMLCanvasElement.prototype.getContext = originalGetContext;
        schedule();
    };

    const capture = () => {
        if (failed || !surface?.dirty || surface.capturing || document.hidden)
            return;
        surface.dirty = false;
        surface.capturing = true;
        // Snapshot without transferToImageBitmap: transferring would clear Skia's drawing buffer.
        let task;
        try {
            task = createImageBitmap(surface.canvas);
        } catch (error) {
            fallback(error);
            return;
        }
        task.then(frame => {
            surface.capturing = false;
            if (failed) {
                frame.close();
                return;
            }
            try {
                worker.postMessage({ type: 'frame', frame }, [frame]);
            } catch (error) {
                frame.close();
                fallback(error);
                return;
            }
            status.captured++;
            if (surface.dirty)
                queueMicrotask(capture);
        }).catch(fallback);
    };
    const armStartupTimeout = () => {
        clearTimeout(startupTimeout);
        startupTimeout = setTimeout(() => {
            if (!workerReady && !document.hidden)
                fallback(new Error('The worker did not start its canvas clock.'));
        }, 5000);
    };
    function getContext(type, options) {
        const appCanvas = this.classList.contains('avalonia-canvas') && !!this.closest('#out');
        if (failed || !appCanvas)
            return originalGetContext.call(this, type, options);
        if (!['webgl', 'webgl2'].includes(type)) {
            fallback(new Error('Worker presentation requires WebGL rendering.'));
            return originalGetContext.call(this, type, options);
        }
        const context = originalGetContext.call(this, type, { ...options, preserveDrawingBuffer: true });
        if (!context || surface?.canvas === this)
            return context;
        if (surface) {
            fallback(new Error('Worker presentation supports one Avalonia canvas.'));
            return context;
        }
        const presenter = document.createElement('canvas');
        presenter.dataset.lumiWorkerPresenter = '';
        presenter.setAttribute('aria-hidden', 'true');
        presenter.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;pointer-events:none';
        this.after(presenter);
        surface = {
            canvas: this, presenter, context, opacity: this.style.opacity,
            dirty: false, capturing: false, captureQueued: false, originals: new Map()
        };
        try {
            const offscreen = presenter.transferControlToOffscreen();
            worker.postMessage({ type: 'init', canvas: offscreen }, [offscreen]);
            for (const name of ['clear', 'clearBufferfv', 'clearBufferiv', 'clearBufferuiv',
                'clearBufferfi', 'drawArrays', 'drawElements', 'drawArraysInstanced',
                'drawElementsInstanced', 'drawRangeElements', 'blitFramebuffer']) {
                const original = context[name];
                if (typeof original !== 'function')
                    continue;
                surface.originals.set(name, original);
                context[name] = function (...args) {
                    const result = original.apply(this, args);
                    surface.dirty = true;
                    if (!surface.captureQueued) {
                        surface.captureQueued = true;
                        // Managed render passes are synchronous; snapshot after the entire pass, not each draw.
                        queueMicrotask(() => {
                            surface.captureQueued = false;
                            lastDraw = performance.now();
                            schedule();
                            capture();
                        });
                    }
                    return result;
                };
            }
            armStartupTimeout();
        } catch (error) {
            fallback(error);
        }
        return context;
    }

    const isAppTarget = target => surface &&
        (target === surface.canvas || target === surface.canvas.parentElement ||
            target?.parentElement === surface.canvas.parentElement &&
            target.classList?.contains('avalonia-native-host'));
    listen(window, 'pointerdown', event => {
        if (event.pointerType !== 'touch' || !workerReady || !isAppTarget(event.target))
            return;
        touches.set(event.pointerId, {
            event, target: event.target,
            samples: [{ time: event.timeStamp, x: event.clientX, y: event.clientY }],
            offsetX: event.offsetX - event.clientX, offsetY: event.offsetY - event.clientY,
            x: event.clientX, y: event.clientY, lastTime: event.timeStamp, moved: false
        });
        schedule();
    }, { capture: true, passive: true });
    listen(window, 'pointermove', event => {
        const touch = !dispatching && touches.get(event.pointerId);
        if (!touch)
            return;
        event.stopImmediatePropagation();
        for (const sample of [...(event.getCoalescedEvents?.() ?? []), event]) {
            if (sample.timeStamp > touch.samples.at(-1).time)
                touch.samples.push({ time: sample.timeStamp, x: sample.clientX, y: sample.clientY });
        }
        if (touch.samples.length > 64)
            touch.samples.splice(0, touch.samples.length - 64);
        Object.assign(touch, {
            event, target: event.target, moved: true,
            offsetX: event.offsetX - event.clientX, offsetY: event.offsetY - event.clientY
        });
    }, { capture: true });
    const lift = event => {
        const touch = touches.get(event.pointerId);
        if (!touch)
            return;
        touches.delete(event.pointerId);
        if (event.type === 'pointerup') {
            touch.samples.push({ time: event.timeStamp, x: event.clientX, y: event.clientY });
            Object.assign(touch, {
                event, target: event.target,
                offsetX: event.offsetX - event.clientX, offsetY: event.offsetY - event.clientY
            });
        }
        flushTouch(touch);
        schedule();
    };
    listen(window, 'pointerup', lift, { capture: true, passive: true });
    listen(window, 'pointercancel', lift, { capture: true, passive: true });
    listen(window, 'lostpointercapture', lift, { capture: true, passive: true });
    listen(document, 'visibilitychange', () => {
        for (const touch of touches.values())
            flushTouch(touch);
        touches.clear();
        if (!document.hidden) {
            lastDraw = performance.now();
            if (surface) {
                surface.dirty = true;
                queueMicrotask(capture);
                if (!workerReady)
                    armStartupTimeout();
            }
        }
        schedule();
    });

    try {
        worker = new Worker(new URL('./workerFramePresenter.js', import.meta.url), { type: 'module' });
        worker.onerror = event => {
            event.preventDefault?.();
            fallback(new Error(event.message || 'The worker failed.'));
        };
        worker.onmessageerror = () => fallback(new Error('The worker message could not be read.'));
        worker.onmessage = ({ data }) => {
            if (failed)
                return;
            if (data.error) {
                fallback(new Error(data.error));
                return;
            }
            if (data.presented) {
                status.presented = data.presented;
                if (status.state !== 'active') {
                    surface.canvas.style.opacity = '0';
                    status.state = 'active';
                    if (badge)
                        badge.textContent = 'Worker renderer';
                }
            }
            if (!Number.isFinite(data.vsync))
                return;
            clearTimeout(startupTimeout);
            workerReady = true;
            lastMessage = performance.now();
            const time = data.vsync - performance.timeOrigin;
            const interval = time - lastTick;
            lastTick = time;
            if (interval > 3 && interval < 40) {
                periods.push(interval);
                if (periods.length > 32)
                    periods.shift();
                period = [...periods].sort((a, b) => a - b)[periods.length >> 1];
                status.clockHz = Math.round(1000 / period);
            }
            if (badge && performance.now() - lastBadge > 1000 && status.state === 'active' && status.clockHz) {
                const text = `Worker renderer (${status.clockHz} Hz clock)`;
                if (badge.textContent !== text)
                    badge.textContent = text;
                lastBadge = performance.now();
            }
            schedule();
            if (!status.clockActive)
                return;
            if (!skippedLast && performance.now() - (data.sent - performance.timeOrigin) > 1.5 * period) {
                skippedLast = true;
                return;
            }
            skippedLast = false;
            for (const touch of touches.values()) {
                if (!touch.moved)
                    continue;
                const sampleTime = time - 12;
                const point = samplePointerPosition(touch.samples, sampleTime);
                if (Math.abs(point.x - touch.x) > 0.01 || Math.abs(point.y - touch.y) > 0.01)
                    dispatchMove(touch, point.x, point.y, sampleTime);
            }
            const behind = performance.now() - time;
            runCallbacks(time + (behind > 1.5 * period ? Math.floor(behind / period) * period : 0));
            schedule();
        };
        HTMLCanvasElement.prototype.getContext = getContext;
    } catch (error) {
        fallback(error);
    }
    return status;
}
