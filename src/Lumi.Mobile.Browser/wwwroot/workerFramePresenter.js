// Adapted from https://github.com/aumb/flutter_shim.
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

let canvas, context, pendingFrame, running = false, wanted = false, presented = 0;

const tick = time => {
    running = false;
    try {
        if (pendingFrame) {
            const frame = pendingFrame;
            pendingFrame = null;
            if (canvas.width !== frame.width)
                canvas.width = frame.width;
            if (canvas.height !== frame.height)
                canvas.height = frame.height;
            try {
                context.transferFromImageBitmap(frame);
            } finally {
                frame.close();
            }
            presented++;
        }
        postMessage({
            vsync: performance.timeOrigin + time,
            sent: performance.timeOrigin + performance.now(),
            presented
        });
        if (wanted || pendingFrame)
            start();
    } catch (error) {
        pendingFrame?.close();
        pendingFrame = null;
        wanted = false;
        postMessage({ error: String(error?.message || error) });
    }
};
const start = () => {
    if (!running) {
        running = true;
        requestAnimationFrame(tick);
    }
};

self.onmessage = ({ data }) => {
    try {
        switch (data.type) {
            case 'init':
                if (typeof requestAnimationFrame !== 'function')
                    throw new Error('Animation frames are unavailable in this worker.');
                canvas = data.canvas;
                context = canvas.getContext('bitmaprenderer');
                if (!context)
                    throw new Error('Worker bitmap presentation is unavailable.');
                start();
                break;
            case 'wanted':
                wanted = data.value;
                if (wanted)
                    start();
                break;
            case 'frame':
                if (!context) {
                    data.frame.close();
                    throw new Error('The worker canvas has not been initialized.');
                }
                // Keep only the newest unshown frame: GPU/main-thread stalls must not build latency.
                pendingFrame?.close();
                pendingFrame = data.frame;
                start();
                break;
        }
    } catch (error) {
        pendingFrame?.close();
        pendingFrame = null;
        wanted = false;
        postMessage({ error: String(error?.message || error) });
    }
};
