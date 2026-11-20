import assert from 'node:assert/strict';
import { beforeEach, test } from 'node:test';

let browserHost;
let recovery;
let message;
let host;
let reloadButton;
let reload;
let errors;
let bodyChildren;
let topLayer;

beforeEach(async t => {
    bodyChildren = [];
    topLayer = [];
    const element = tagName => {
        const listeners = new Map();
        return {
            tagName: tagName.toUpperCase(), style: {}, value: '', disabled: false, open: false,
            classList: { add() {}, remove() {}, toggle() {} },
            addEventListener: t.mock.fn((name, callback) => listeners.set(name, callback)),
            dispatch(name, event = {}) { listeners.get(name)?.(event); },
            append() {}, setAttribute() {}, select() {},
            setSelectionRange(start, end) {
                this.selectionStart = start;
                this.selectionEnd = end;
            },
            setRangeText(value, start, end) {
                this.value = this.value.slice(0, start) + value + this.value.slice(end);
            },
            focus() {
                if (this.disabled)
                    return;
                document.activeElement?.blur();
                document.activeElement = this;
                this.dispatch('focus');
            },
            blur() {
                if (document.activeElement !== this)
                    return;
                document.activeElement = undefined;
                this.dispatch('blur');
            },
            showModal: t.mock.fn(function () { this.open = true; topLayer.push(this); }),
            close() { this.open = false; topLayer = topLayer.filter(item => item !== this); },
            remove() { bodyChildren = bodyChildren.filter(item => item !== this); }
        };
    };
    recovery = Object.assign(element('dialog'), { hidden: true });
    message = { textContent: '' };
    host = { addEventListener: t.mock.fn() };
    reloadButton = element('button');
    reload = t.mock.fn();
    const elements = {
        out: host,
        'lumi-recovery': recovery,
        'lumi-recovery-message': message,
        'lumi-reload': reloadButton
    };
    const document = {
        createElement: element,
        body: { appendChild: child => bodyChildren.push(child) },
        documentElement: { appendChild() {} },
        addEventListener() {},
        getElementById: id => elements[id]
    };
    for (const [name, value] of Object.entries({
        document,
        navigator: { standalone: false },
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
    browserHost = await import(`../../src/Lumi.Mobile.Browser/wwwroot/browserHost.js?case=${encodeURIComponent(t.name)}`);
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

function showEditor(id = 1, revision = 0) {
    browserHost.showNativeTextInput(id, 0, 0, 200, 40, 0, 0, 200, 40,
        '123456', 'Pairing code', false, 6, 'numeric', 'go', false, false, false,
        true, 'Inter', 16, 400, 'normal', 24, 0, 4, 4, 4, 4, 'start', 'ltr', false, revision);
}

test('multiline fields that do not forward Enter leave rapid typing and newlines to the browser', t => {
    const keyed = t.mock.fn(async () => false);
    browserHost.configureNativeTextInputs(t.mock.fn(async () => {}), keyed, t.mock.fn());
    browserHost.showNativeTextInput(1, 0, 0, 200, 80, 0, 0, 200, 80,
        'Initial', 'Skill content', true, 0, 'text', 'enter', false, false, false,
        true, 'Inter', 16, 400, 'normal', 24, 0, 4, 4, 4, 4, 'start', 'ltr', false, 0, false);
    const prevented = t.mock.fn();
    const input = bodyChildren.find(item => item.tagName === 'TEXTAREA');
    input.dispatch('keydown', { key: 'Enter', preventDefault: prevented });
    assert.equal(keyed.mock.callCount(), 0);
    assert.equal(prevented.mock.callCount(), 0);
});

test('a forwarded but unhandled Enter keeps text typed before the managed answer', async t => {
    let decide;
    const changed = t.mock.fn(async () => {});
    browserHost.configureNativeTextInputs(changed, () => new Promise(resolve => { decide = resolve; }), t.mock.fn());
    browserHost.showNativeTextInput(1, 0, 0, 200, 80, 0, 0, 200, 80,
        'first', 'Message Lumi', true, 0, 'text', 'enter', false, false, false,
        true, 'Inter', 16, 400, 'normal', 24, 0, 4, 4, 4, 4, 'start', 'ltr', false, 0, true);
    const input = bodyChildren.find(item => item.tagName === 'TEXTAREA');
    input.focus();
    input.setSelectionRange(5, 5);
    const prevented = t.mock.fn();
    input.dispatch('keydown', { key: 'Enter', preventDefault: prevented });
    assert.equal(prevented.mock.callCount(), 1);
    input.value = 'firstsecond';
    input.setSelectionRange(11, 11);
    input.dispatch('input');
    decide(false);
    await Promise.resolve();
    assert.equal(input.value, 'first\nsecond');
    assert.equal(changed.mock.calls.at(-1).arguments[1], 'first\nsecond');
});

test('recovery owns focus and blocks native input callbacks and later refocusing', t => {
    const changed = t.mock.fn();
    const keyed = t.mock.fn();
    const focused = t.mock.fn();
    browserHost.configureNativeTextInputs(changed, keyed, focused);
    showEditor();
    const input = bodyChildren.find(item => item.tagName === 'INPUT');
    browserHost.focusNativeTextInput(1, 0);
    assert.equal(document.activeElement, input);
    input.dispatch('keydown', { key: 'Enter', preventDefault() {} });
    assert.equal(keyed.mock.callCount(), 1);
    changed.mock.resetCalls();
    keyed.mock.resetCalls();
    focused.mock.resetCalls();

    browserHost.configureBrowserRecovery();
    loseContext();
    assert.equal(document.activeElement, reloadButton);
    assert.equal(input.disabled, true);
    assert.equal(topLayer.at(-1), recovery);
    input.dispatch('keydown', { key: 'Enter', preventDefault() {} });
    input.dispatch('input');
    input.dispatch('compositionend');
    showEditor();
    showEditor(2);
    browserHost.focusNativeTextInput(1, 0);
    assert.equal(document.activeElement, reloadButton);
    assert.equal(input.disabled, true);
    assert.equal(bodyChildren.filter(item => item.tagName === 'INPUT').length, 1);
    assert.equal(changed.mock.callCount(), 0);
    assert.equal(keyed.mock.callCount(), 0);
    assert.equal(focused.mock.callCount(), 0);
});

test('a starter blank is selected in the native editor, and a range past the text is clamped', t => {
    browserHost.configureNativeTextInputs(t.mock.fn(), t.mock.fn(), t.mock.fn());
    showEditor();
    const input = bodyChildren.find(item => item.tagName === 'INPUT');
    input.setSelectionRange = t.mock.fn();

    browserHost.selectNativeTextInput(1, 2, 5);
    assert.equal(document.activeElement, input);
    assert.deepEqual(input.setSelectionRange.mock.calls.at(-1).arguments, [2, 5]);

    browserHost.selectNativeTextInput(1, 4, 99);
    assert.deepEqual(input.setSelectionRange.mock.calls.at(-1).arguments, [4, 6]);

    browserHost.focusNativeTextInput(1, 3);
    assert.deepEqual(input.setSelectionRange.mock.calls.at(-1).arguments, [3, 3]);
});

test('a late layout or blur cannot overwrite text still crossing to the managed editor', async t => {
    const pending = [];
    const changed = t.mock.fn(() => new Promise(resolve => pending.push(resolve)));
    browserHost.configureNativeTextInputs(changed, undefined, undefined);
    showEditor();
    const input = bodyChildren.find(item => item.tagName === 'INPUT');
    input.focus();
    input.value = '654321';
    input.dispatch('input');
    showEditor();
    assert.equal(input.value, '654321');

    input.blur();
    assert.equal(changed.mock.calls.at(-1).arguments[1], '654321');
    assert.equal(changed.mock.callCount(), 1, 'A blur must not resend text that Send could have cleared.');

    assert.equal(browserHost.hasPendingNativeInput(), true);
    // Managed code can clear or replace the text after it sees the newest edit, even before
    // the browser receives the acknowledgement for that edit.
    showEditor(1, changed.mock.calls.at(-1).arguments[3]);
    assert.equal(input.value, '123456');
    pending[0]();
    await Promise.resolve();
    assert.equal(browserHost.hasPendingNativeInput(), false);
});

test('recovery dismisses the selection modal and prevents it reopening over Reload', () => {
    browserHost.showTextSelection('Selected message');
    const selection = topLayer.at(-1);
    assert.equal(selection.open, true);
    browserHost.configureBrowserRecovery();
    loseContext();
    assert.equal(selection.open, false);
    assert.ok(!bodyChildren.includes(selection));
    assert.deepEqual(topLayer, [recovery]);
    browserHost.showTextSelection('Late selection callback');
    assert.deepEqual(topLayer, [recovery]);
    assert.equal(document.activeElement, reloadButton);
});

test('Escape cannot dismiss recovery and repeated failures do not reopen its modal', t => {
    browserHost.configureBrowserRecovery();
    loseContext();
    const preventDefault = t.mock.fn();
    recovery.dispatch('cancel', { preventDefault });
    assert.equal(preventDefault.mock.callCount(), 1);
    loseContext();
    assert.equal(recovery.showModal.mock.callCount(), 1);
    assert.equal(document.activeElement, reloadButton);
});

test('explicit Reload uses the existing host recovery callback when supplied', t => {
    const recover = t.mock.fn();
    browserHost.configureBrowserRecovery(recover);
    loseContext();
    reloadButton.dispatch('click');
    assert.equal(recover.mock.callCount(), 1);
    assert.equal(reload.mock.callCount(), 0);
});
