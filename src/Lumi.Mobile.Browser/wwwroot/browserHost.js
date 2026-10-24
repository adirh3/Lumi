const safeAreaProbe = document.createElement('div');
const uiText = (key, fallback) => globalThis.lumiLocalization?.text(key, fallback) ?? fallback;
safeAreaProbe.className = 'safe-area-probe';
document.documentElement.appendChild(safeAreaProbe);

if (navigator.standalone === true)
    document.documentElement.dataset.lumiStandalone = 'true';

let browserRecoveryActive = false;

export function showBrowserRecovery(message) {
    browserRecoveryActive = true;
    dismissTextSelection();
    for (const input of nativeTextInputs.values()) {
        input.blur();
        input.disabled = true;
    }
    activeNativeTextInputId = undefined;
    document.getElementById('lumi-recovery-message').textContent = message;
    const recovery = document.getElementById('lumi-recovery');
    recovery.hidden = false;
    if (!recovery.open)
        recovery.showModal();
    document.getElementById('lumi-reload').focus();
}

export function configureBrowserRecovery(reload = () => globalThis.location.reload()) {
    document.getElementById('lumi-reload').addEventListener('click', reload);
    document.getElementById('lumi-recovery').addEventListener('cancel', event => event.preventDefault());
    // Context-loss events do not bubble. Listen on the host before Avalonia creates its canvas.
    document.getElementById('out').addEventListener('webglcontextlost', event => {
        if (event.target.tagName !== 'CANVAS'
            || !event.target.classList.contains('avalonia-canvas'))
            return;

        console.error('[Lumi] The browser graphics context was lost.');
        showBrowserRecovery(uiText('WebContextLost', 'Your browser stopped displaying Lumi. Reload to reconnect.'));
    }, true);
}

export function getOrigin() {
    return globalThis.location.origin;
}

export function getLanguage() {
    return globalThis.lumiLocalization?.language ?? 'en';
}

export function getStorageItem(key) {
    try {
        return globalThis.localStorage.getItem(key);
    } catch {
        return null;
    }
}

export function setStorageItem(key, value) {
    try {
        globalThis.localStorage.setItem(key, value);
    } catch {
    }
}

export function createObjectUrl(bytes, contentType) {
    return URL.createObjectURL(new Blob([bytes], {
        type: contentType || 'application/octet-stream'
    }));
}

export function revokeObjectUrl(url) {
    if (url?.startsWith('blob:'))
        URL.revokeObjectURL(url);
}

export function configureFilePickerCancellation({ StorageProvider }) {
    const openFileDialog = StorageProvider.openFileDialog;
    StorageProvider.openFileDialog = async function (...args) {
        let input;
        const canceled = () => {
            // Avalonia's fallback waits for change only; a native cancel must finish that wait too.
            if (input.files.length === 0)
                input.dispatchEvent(new Event('change'));
        };
        const capturePicker = event => {
            const target = event.target;
            if (!input && target?.tagName === 'INPUT' && target.type === 'file') {
                input = target;
                input.addEventListener('cancel', canceled);
            }
        };
        document.addEventListener('click', capturePicker, true);
        try {
            const operation = openFileDialog.apply(this, args);
            document.removeEventListener('click', capturePicker, true);
            return await operation;
        } finally {
            document.removeEventListener('click', capturePicker, true);
            input?.removeEventListener('cancel', canceled);
            input?.remove();
        }
    };
}

let selectionDialog;
let nativeTextInputChanged;
let nativeTextInputKeyPressed;
let nativeTextInputFocusChanged;
let activeNativeTextInputId;
const nativeTextInputs = new Map();
const composingNativeTextInputs = new Set();

function publishNativeTextInput(id, input) {
    if (browserRecoveryActive)
        return;
    nativeTextInputChanged?.(
        id,
        input.value,
        input.selectionStart ?? input.value.length);
}

document.addEventListener('focusin', event => {
    if (activeNativeTextInputId !== undefined
        && event.target?.classList?.contains('avalonia-input-element')) {
        queueMicrotask(() => focusNativeTextInput(
            activeNativeTextInputId,
            getNativeTextInputCaretIndex(activeNativeTextInputId)));
    }
}, true);

document.addEventListener('pointerdown', event => {
    if (activeNativeTextInputId === undefined)
        return;

    const input = nativeTextInputs.get(activeNativeTextInputId);
    if (!input || event.target === input)
        return;

    // iOS can commit the final autocorrect/composition replacement only as focus leaves the
    // textarea. Publish the DOM value before Avalonia handles the button click so Send never
    // captures the previous marked-text snapshot.
    publishNativeTextInput(activeNativeTextInputId, input);
}, true);

export function showTextSelection(text) {
    if (browserRecoveryActive)
        return;
    dismissTextSelection();
    selectionDialog = document.createElement('dialog');
    selectionDialog.className = 'lumi-selection-dialog';
    const editor = document.createElement('textarea');
    editor.readOnly = true;
    editor.value = text;
    editor.setAttribute('aria-label', uiText('MessageText', 'Message text'));
    editor.setAttribute('dir', 'auto');
    const close = document.createElement('button');
    close.type = 'button';
    close.textContent = uiText('Done', 'Done');
    close.addEventListener('click', dismissTextSelection);
    selectionDialog.append(editor, close);
    selectionDialog.addEventListener('cancel', event => {
        event.preventDefault();
        dismissTextSelection();
    });
    document.body.appendChild(selectionDialog);
    selectionDialog.showModal();
    editor.focus();
    editor.select();
}

export function dismissTextSelection() {
    if (!selectionDialog)
        return;
    selectionDialog.close();
    selectionDialog.remove();
    selectionDialog = undefined;
}

function applyInputClip(input, x, y, width, height, clipX, clipY, clipWidth, clipHeight) {
    const top = Math.max(0, clipY - y);
    const right = Math.max(0, (x + width) - (clipX + clipWidth));
    const bottom = Math.max(0, (y + height) - (clipY + clipHeight));
    const left = Math.max(0, clipX - x);
    const clip = `inset(${top}px ${right}px ${bottom}px ${left}px)`;
    input.style.clipPath = clip;
    input.style.webkitClipPath = clip;
}

export function configureNativeTextInputs(onChanged, onKeyPressed, onFocusChanged) {
    nativeTextInputChanged = onChanged;
    nativeTextInputKeyPressed = onKeyPressed;
    nativeTextInputFocusChanged = onFocusChanged;
}

function createNativeTextInput(id, multiline) {
    const input = document.createElement(multiline ? 'textarea' : 'input');
    input.className = 'lumi-native-text-input-overlay';
    if (!multiline)
        input.type = 'text';
    input.addEventListener('beforeinput', event => {
        if (event.isComposing || event.inputType === 'insertCompositionText')
            composingNativeTextInputs.add(id);
    });
    input.addEventListener('input', event => {
        if (event.isComposing)
            composingNativeTextInputs.add(id);
        publishNativeTextInput(id, input);
    });
    input.addEventListener('compositionstart', () => {
        composingNativeTextInputs.add(id);
    });
    input.addEventListener('compositionend', () => {
        composingNativeTextInputs.delete(id);
        publishNativeTextInput(id, input);
    });
    input.addEventListener('keydown', event => {
        if (browserRecoveryActive) {
            event.preventDefault();
            return;
        }
        if (event.isComposing
            || event.keyCode === 229
            || event.key !== 'Enter' && event.key !== 'Escape')
            return;
        publishNativeTextInput(id, input);
        if (nativeTextInputKeyPressed?.(
            id,
            event.key,
            event.ctrlKey,
            event.altKey,
            event.shiftKey,
            event.metaKey)) {
            event.preventDefault();
        }
    });
    input.addEventListener('focus', () => {
        if (browserRecoveryActive)
            return;
        activeNativeTextInputId = id;
        input.classList.add('focused');
        nativeTextInputFocusChanged?.(id, true);
    });
    input.addEventListener('blur', () => {
        composingNativeTextInputs.delete(id);
        publishNativeTextInput(id, input);
        if (activeNativeTextInputId === id)
            activeNativeTextInputId = undefined;
        input.classList.remove('focused');
        if (!browserRecoveryActive)
            nativeTextInputFocusChanged?.(id, false);
    });
    document.body.appendChild(input);
    nativeTextInputs.set(id, input);
    return input;
}

export function showNativeTextInput(
    id,
    x,
    y,
    width,
    height,
    clipX,
    clipY,
    clipWidth,
    clipHeight,
    value,
    placeholder,
    multiline,
    maxLength,
    inputMode,
    enterKeyHint,
    sensitive,
    autoCapitalization,
    showSuggestions,
    enabled,
    fontFamily,
    fontSize,
    fontWeight,
    fontStyle,
    lineHeight,
    letterSpacing,
    paddingTop,
    paddingRight,
    paddingBottom,
    paddingLeft,
    textAlignment,
    direction,
    dark) {
    if (browserRecoveryActive)
        return;
    let input = nativeTextInputs.get(id);
    if (input && (input.tagName === 'TEXTAREA') !== multiline) {
        destroyNativeTextInput(id);
        input = undefined;
    }
    input ??= createNativeTextInput(id, multiline);

    input.style.display = 'block';
    input.style.left = `${x}px`;
    input.style.top = `${y}px`;
    input.style.width = `${width}px`;
    input.style.height = `${height}px`;
    applyInputClip(
        input,
        x,
        y,
        width,
        height,
        clipX,
        clipY,
        clipWidth,
        clipHeight);
    input.placeholder = placeholder || '';
    input.inputMode = inputMode || 'text';
    input.enterKeyHint = enterKeyHint || 'enter';
    input.maxLength = maxLength > 0 ? maxLength : 524288;
    input.autocomplete = sensitive ? 'off' : inputMode === 'search' ? 'off' : 'on';
    input.autocapitalize = autoCapitalization ? 'sentences' : 'none';
    input.spellcheck = !!showSuggestions;
    input.disabled = !enabled;
    input.style.fontFamily = `"${fontFamily}", system-ui, -apple-system, BlinkMacSystemFont, sans-serif`;
    // Safari zooms and pans the page when focusing an editor smaller than 16 CSS pixels.
    input.style.fontSize = `${Math.max(16, fontSize)}px`;
    input.style.fontWeight = `${fontWeight}`;
    input.style.fontStyle = fontStyle || 'normal';
    input.style.lineHeight = lineHeight > 0 ? `${lineHeight}px` : 'normal';
    input.style.letterSpacing = `${letterSpacing}px`;
    input.style.padding = `${paddingTop}px ${paddingRight}px ${paddingBottom}px ${paddingLeft}px`;
    input.style.textAlign = textAlignment || 'start';
    input.dir = direction || 'ltr';
    input.classList.toggle('dark', dark);
    if (!multiline)
        input.type = sensitive ? 'password' : 'text';
    input.setAttribute('aria-label', placeholder || uiText('TextInput', 'Text input'));

    if (!composingNativeTextInputs.has(id) && input.value !== value) {
        const caret = input.selectionStart ?? value.length;
        input.value = value;
        const clamped = Math.min(caret, value.length);
        input.setSelectionRange(clamped, clamped);
    }
}

export function hideNativeTextInput(id) {
    const input = nativeTextInputs.get(id);
    if (!input)
        return;
    if (document.activeElement === input)
        input.blur();
    input.style.display = 'none';
}

export function blurNativeTextInput(id) {
    const input = nativeTextInputs.get(id);
    if (input && document.activeElement === input)
        input.blur();
}

export function destroyNativeTextInput(id) {
    const input = nativeTextInputs.get(id);
    if (!input)
        return;
    if (activeNativeTextInputId === id)
        activeNativeTextInputId = undefined;
    composingNativeTextInputs.delete(id);
    nativeTextInputs.delete(id);
    input.remove();
}

export function getNativeTextInputCaretIndex(id) {
    return nativeTextInputs.get(id)?.selectionStart ?? 0;
}

export function focusNativeTextInput(id, caretIndex) {
    if (browserRecoveryActive)
        return;
    const input = nativeTextInputs.get(id);
    if (!input
        || input.disabled
        || input.style.display === 'none'
        || composingNativeTextInputs.has(id))
    {
        return;
    }
    activeNativeTextInputId = id;
    input.focus({ preventScroll: true });
    const clamped = Math.max(0, Math.min(caretIndex, input.value.length));
    input.setSelectionRange(clamped, clamped);
}

export function downloadUrl(url, fileName) {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    anchor.rel = 'noopener';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    if (url.startsWith('blob:'))
        setTimeout(() => URL.revokeObjectURL(url), 60000);
}

export function publishViewportInsets(callback) {
    const style = getComputedStyle(safeAreaProbe);
    const top = parseFloat(style.paddingTop) || 0;
    const right = parseFloat(style.paddingRight) || 0;
    const bottom = parseFloat(style.paddingBottom) || 0;
    const left = parseFloat(style.paddingLeft) || 0;
    const bounds = document.getElementById('out').getBoundingClientRect();
    const viewport = window.visualViewport;
    const active = document.activeElement;
    const editing = active?.isContentEditable
        || active && !active.readOnly && !active.disabled
        && (active.tagName === 'INPUT' || active.tagName === 'TEXTAREA');
    // The dynamic-height canvas can already exclude Safari chrome or a resized keyboard.
    // Only inset what still overlaps it, using the same CSS-pixel coordinates as the editor.
    const overlap = editing && viewport && Math.abs(viewport.scale - 1) < 0.01
        ? Math.max(0, bounds.bottom - viewport.height - viewport.offsetTop)
        : 0;
    // Standalone Safari can exclude the home indicator from visualViewport, even with no IME.
    const keyboardInset = overlap > bottom + 1 ? overlap : 0;
    callback(top, right, bottom, left, keyboardInset, bounds.height);
}

export function configureViewportInsets(callback) {
    let pendingFrame;
    const publish = () => publishViewportInsets(callback);
    const schedule = () => {
        if (document.hidden || pendingFrame !== undefined)
            return;
        pendingFrame = window.requestAnimationFrame(() => {
            pendingFrame = undefined;
            publish();
        });
    };

    window.addEventListener('resize', schedule);
    window.addEventListener('orientationchange', schedule);
    window.addEventListener('pageshow', schedule);
    window.visualViewport?.addEventListener('resize', schedule);
    window.visualViewport?.addEventListener('scroll', schedule);
    document.addEventListener('focusin', schedule);
    document.addEventListener('focusout', schedule);
    document.addEventListener('visibilitychange', schedule);
    if (typeof ResizeObserver === 'function')
        new ResizeObserver(schedule).observe(document.getElementById('out'));
    publish();
}
