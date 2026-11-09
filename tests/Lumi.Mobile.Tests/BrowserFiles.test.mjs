import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/Lumi.Mobile.Browser/wwwroot/browserHost.js', import.meta.url), 'utf8');

function host({ canShare = true, shareError } = {}) {
    const blobs = new Map();
    const revoked = [];
    const clicked = [];
    const shared = [];
    const timers = [];
    const document = {
        documentElement: { appendChild() {} },
        body: { appendChild() {} },
        addEventListener() {},
        createElement(tag) {
            return {
                tagName: tag, style: {}, attributes: {},
                isConnected: true,
                contentWindow: { postMessage(data, origin) { this.message = { data, origin }; } },
                addEventListener(name, callback, options) { this[name] = callback; this[`${name}Options`] = options; },
                setAttribute(name, value) { this.attributes[name] = value; },
                click() { clicked.push({ href: this.href, download: this.download }); },
                remove() { this.removed = true; this.isConnected = false; }
            };
        }
    };
    const context = vm.createContext({
        document, Blob, File, Uint8Array,
        URL: {
            createObjectURL(blob) {
                const url = `blob:lumi/${blobs.size}`;
                blobs.set(url, blob);
                return url;
            },
            revokeObjectURL(url) { revoked.push(url); }
        },
        navigator: {
            canShare: ({ files }) => typeof canShare === 'function' ? canShare(files[0]) : canShare,
            share({ files }) {
                shared.push(files[0]);
                return shareError ? Promise.reject(shareError) : Promise.resolve();
            }
        },
        setTimeout(callback) { timers.push(callback); },
        fetch() { throw new Error('Local file actions must not fetch after the tap.'); }
    });
    vm.runInContext(source.replace(/^export /gm, ''), context);
    return { context, blobs, revoked, clicked, shared, timers };
}

const bytes = new TextEncoder().encode('<!doctype html><meta charset="utf-8"><h1>Report \u05e9\u05dc\u05d5\u05dd</h1>');

test('the renderer accepts one byte payload from its parent, never messages from other frames', () => {
    const shell = readFileSync(new URL('../../src/Lumi.Mobile.Browser/wwwroot/html-preview.html', import.meta.url), 'utf8');
    let receive;
    let removed = false;
    let rendered;
    const parent = {};
    const context = vm.createContext({
        parent, Uint8Array, TextDecoder,
        addEventListener(name, callback) { assert.equal(name, 'message'); receive = callback; },
        removeEventListener(name, callback) { assert.equal(callback, receive); removed = true; },
        document: { open() {}, write(html) { rendered = html; }, close() {} }
    });
    vm.runInContext(shell.match(/<script>([\s\S]*?)<\/script>/)[1], context);
    receive({ source: {}, data: bytes });
    receive({ source: parent, data: 'not bytes' });
    assert.equal(rendered, undefined);
    assert.equal(removed, false);
    receive({ source: parent, data: bytes });
    assert.equal(rendered, new TextDecoder().decode(bytes));
    assert.equal(removed, true);
});

test('HTML reaches only the isolated renderer after it loads and is removed on close', () => {
    const h = host();
    const frame = h.context.createHtmlPreview(bytes, 'report.html');
    assert.equal(frame.tagName, 'iframe');
    assert.equal(frame.attributes.sandbox, 'allow-scripts allow-downloads');
    assert.equal(frame.referrerPolicy, 'no-referrer');
    assert.equal(frame.title, 'report.html');
    assert.equal(frame.style.width, '100%');
    assert.equal(frame.style.height, '100%');
    assert.equal(frame.src, '/app/html-preview.html');
    assert.equal(frame.contentWindow.message, undefined);
    assert.equal(frame.loadOptions.once, true);
    frame.load();
    assert.equal(frame.contentWindow.message.data, bytes);
    assert.equal(frame.contentWindow.message.origin, '*', 'the sandbox has an opaque origin');
    h.context.destroyHtmlPreview(frame);
    assert.equal(frame.src, 'about:blank');
    assert.equal(frame.removed, true);
    const unopened = h.context.createHtmlPreview(bytes, 'other.html');
    h.context.destroyHtmlPreview(unopened);
    unopened.load();
    assert.equal(unopened.contentWindow.message, undefined);
    assert.equal(h.blobs.size, 0);
});

test('download preserves exact bytes and name', async () => {
    const h = host();
    h.context.downloadFile(bytes, 'my report.HTML');
    const download = h.clicked[0];
    assert.equal(download.download, 'my report.HTML');
    const blob = h.blobs.get(download.href);
    assert.equal(blob.type, 'text/html');
    assert.deepEqual(new Uint8Array(await blob.arrayBuffer()), bytes);
    assert.equal(h.revoked.length, 0, 'the browser must have time to consume the download');
    h.timers[0]();
    assert.deepEqual(h.revoked, [download.href]);
});

test('sharing calls the native sheet synchronously, with original HTML bytes, type and name', async () => {
    const h = host();
    const result = h.context.shareBytes(bytes, 'report.htm');
    assert.equal(h.shared.length, 1, 'no fetch or await may consume the user activation first');
    assert.equal(await result, 'shared');
    assert.equal(h.shared[0].name, 'report.htm');
    assert.equal(h.shared[0].type, 'text/html');
    assert.deepEqual(new Uint8Array(await h.shared[0].arrayBuffer()), bytes);
    assert.equal(h.blobs.size, 0, 'sharing bytes needs no temporary URL');
});

test('the share button respects the browser file-type restriction, not just image support', async () => {
    const h = host({ canShare: file => file.type === 'image/png' });
    assert.equal(h.context.canShareFiles(), true);
    assert.equal(h.context.canShareFile('report.html'), false);
    assert.equal(await h.context.shareBytes(bytes, 'report.html'), 'unsupported');
    assert.equal(h.shared.length, 0);
    h.context.downloadFile(bytes, 'report.html');
    assert.equal(h.clicked.length, 1, 'Download remains available');
});

test('cancelling the share sheet is distinct from a failed handoff', async () => {
    for (const [name, result] of [['AbortError', 'cancelled'], ['NotAllowedError', 'failed']]) {
        const h = host({ shareError: { name } });
        assert.equal(await h.context.shareBytes(bytes, 'report.html'), result);
    }
});
