import assert from 'node:assert/strict';
import { beforeEach, test } from 'node:test';

let browserHost;
let recovery;
let message;
let host;
let reloadButton;
let reload;
let errors;

beforeEach(async t => {
    recovery = { hidden: true };
    message = { textContent: '' };
    host = { addEventListener: t.mock.fn() };
    reloadButton = { addEventListener: t.mock.fn() };
    reload = t.mock.fn();
    const elements = {
        out: host,
        'lumi-recovery': recovery,
        'lumi-recovery-message': message,
        'lumi-reload': reloadButton
    };
    const document = {
        createElement: () => ({}),
        documentElement: { appendChild() {} },
        addEventListener() {},
        getElementById: id => elements[id]
    };
    for (const [name, value] of Object.entries({
        document,
        location: { reload }
    })) {
        const original = Object.getOwnPropertyDescriptor(globalThis, name);
        Object.defineProperty(globalThis, name, { configurable: true, value });
        t.after(() => {
            if (original)
                Object.defineProperty(globalThis, name, original);
            else
                delete globalThis[name];
        });
    }
    errors = t.mock.method(console, 'error', () => {});
    browserHost = await import('../../src/Lumi.Mobile.Browser/wwwroot/browserHost.js');
});

function loseContext(tagName = 'CANVAS', isAvaloniaCanvas = true) {
    const callback = host.addEventListener.mock.calls[0].arguments[1];
    callback({
        target: {
            tagName,
            classList: { contains: name => name === 'avalonia-canvas' && isAvaloniaCanvas }
        }
    });
}

test('captures non-bubbling context loss before the renderer creates its canvas', () => {
    browserHost.configureBrowserRecovery();
    const [eventName, callback, capture] = host.addEventListener.mock.calls[0].arguments;
    assert.equal(eventName, 'webglcontextlost');
    assert.equal(typeof callback, 'function');
    assert.equal(capture, true);
    assert.equal(recovery.hidden, true);
});

test('lost app graphics show a recovery action without silently reloading unsent text', () => {
    browserHost.configureBrowserRecovery();
    loseContext();
    assert.equal(recovery.hidden, false);
    assert.match(message.textContent, /Reload to reconnect/);
    assert.equal(errors.mock.callCount(), 1);
    assert.equal(reload.mock.callCount(), 0);
});

test('graphics events from unrelated elements do not interrupt the app', () => {
    browserHost.configureBrowserRecovery();
    loseContext('DIV');
    loseContext('CANVAS', false);
    assert.equal(recovery.hidden, true);
    assert.equal(errors.mock.callCount(), 0);
});

test('runtime errors remain visible independently of the renderer-hidden startup splash', () => {
    browserHost.showBrowserRecovery('Lumi could not start: <unexpected response>');
    assert.equal(recovery.hidden, false);
    assert.equal(message.textContent, 'Lumi could not start: <unexpected response>');
    assert.equal(reload.mock.callCount(), 0);
});

test('only the explicit recovery button reloads the current page', () => {
    browserHost.configureBrowserRecovery();
    loseContext();
    const [eventName, callback] = reloadButton.addEventListener.mock.calls[0].arguments;
    assert.equal(eventName, 'click');
    callback();
    assert.equal(reload.mock.callCount(), 1);
});
