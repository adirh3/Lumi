using System;
using System.Text.Encodings.Web;
using System.Threading;

namespace Lumi.Services;

/// <summary>The same document registry and resolver are used by every numbered browser operation.</summary>
internal static class BrowserDomScript
{
    internal const long NodesPerScope = 1_000_000;
    internal const long MaxScope = 9_007_199_253 - 1;
    private static long _nextScope;

    internal static string Build(
        string operation, string? target = null, string? value = null, int limit = 50,
        bool preferDialog = true)
    {
        var scope = Interlocked.Increment(ref _nextScope);
        if (scope > MaxScope)
            throw new InvalidOperationException("Browser element reference scopes exhausted.");
        return "(() => {\nconst scope = " + scope + ";\nconst operation = " + Quote(operation) +
            ";\nconst target = " + Quote(target ?? "") +
            ";\nconst value = " + Quote(value ?? "") +
            ";\nconst limit = " + Math.Clamp(limit, 1, 50) +
            ";\nconst preferDialog = " + (preferDialog ? "true" : "false") + ";\n" + Body + "\n})()";
    }

    private static string Quote(string value) => "\"" + JavaScriptEncoder.Default.Encode(value) + "\"";

    private const string Body = """
        const ok = message => ({ok:true, message});
        const fail = message => ({ok:false, message});
        const pending = message => ({ok:false, message, pending:true});
        class AutomationError extends Error {}
        const norm = s => String(s || '').replace(/\s+/g, ' ').trim();
        const selector = 'a[href],button,input,select,textarea,summary,[role="button"],[role="link"],[role="tab"],[role="menuitem"],[role="radio"],[role="checkbox"],[role="switch"],[role="combobox"],[role="option"],[role="gridcell"],[role="spinbutton"],[role="slider"],[onclick],[tabindex],[contenteditable],[data-tooltip]';
        const dialogSelector = 'dialog[open],[role="dialog"],[aria-modal="true"]';
        const fieldSelector = 'input,select,textarea,[contenteditable="true"],[role="textbox"],[role="combobox"]';
        const visible = el => {
            if (!el || !el.isConnected || el.ownerDocument !== document || el.closest('[hidden],[inert]')) return false;
            const box = el.getBoundingClientRect();
            if (box.width <= 0 || box.height <= 0) return false;
            for (let node = el; node && node.nodeType === 1; node = node.parentElement) {
                const style = getComputedStyle(node);
                if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse' || style.opacity === '0') return false;
            }
            return true;
        };
        const slot = Symbol.for('lumi.browser.elements.v1');
        let registry = document[slot];
        if (!registry) {
            registry = {scope, next:0, ids:new WeakMap(), nodes:new Map(), secrets:new Set(), changed:performance.now()};
            Object.defineProperty(document, slot, {value:registry, configurable:true});
            registry.observer = new MutationObserver(() => { registry.changed = performance.now(); });
            registry.observer.observe(document, {subtree:true, childList:true, attributes:true, characterData:true});
            // A restored document gets new references, even though its JS heap survived navigation.
            const restored = event => {
                if (!event.persisted) return;
                registry.observer.disconnect();
                if (document[slot] === registry) delete document[slot];
                removeEventListener('pageshow', restored);
            };
            addEventListener('pageshow', restored);
        }
        const rememberSecrets = () => {
            for (const el of document.querySelectorAll('input[type="password"]')) {
                if (el.value) registry.secrets.add(el.value);
            }
        };
        const redact = text => {
            let result = String(text ?? '');
            for (const secret of [...registry.secrets].sort((a,b) => b.length-a.length))
                if (secret) result = result.split(secret).join('[redacted]');
            return result;
        };
        const errorText = error => error instanceof AutomationError
            ? redact(error.message) : 'The page action failed; it was not retried.';
        const id = el => {
            let number = registry.ids.get(el);
            if (!number) {
                if (registry.next >= 999999) throw new AutomationError('Element reference capacity exhausted.');
                number = registry.scope * 1000000 + (++registry.next);
                registry.ids.set(el, number);
                registry.nodes.set(String(number), new WeakRef(el));
            }
            return number;
        };
        const roots = () => [...document.querySelectorAll(dialogSelector)].filter(visible).reverse().concat(document);
        const collect = (query = selector) => {
            const seen = new Set(), elements = [];
            for (const root of roots()) for (const el of root.querySelectorAll(query)) {
                if (seen.has(el) || !visible(el)) continue;
                seen.add(el); id(el); elements.push(el);
            }
            return elements;
        };
        const label = el => norm([...el.labels || []].map(l => l.innerText || l.textContent).join(' '));
        const searchable = el => [el.tagName, el.type, el.name, el.id, el.placeholder,
            el.getAttribute('aria-label'), el.getAttribute('role'), el.getAttribute('data-tooltip'),
            el.title, el.href, label(el), el.type === 'password' ? '' : el.textContent].map(norm);
        const describe = el => {
            const type = el.tagName === 'INPUT' ? 'input[' + el.type + ']' :
                el.getAttribute('role') || (el.tagName === 'A' ? 'link' : el.tagName.toLowerCase());
            const name = el.name || el.id || el.placeholder || '';
            const text = el.type === 'password' ? '' : norm(el.textContent);
            let line = '[' + id(el) + '] ' + type;
            if (text) line += ' text="' + redact(text).slice(0,80) + '"';
            if (name) line += ' name="' + redact(name).slice(0,80) + '"';
            const aria = el.getAttribute('aria-label'), tip = el.getAttribute('data-tooltip') || el.title;
            if (aria) line += ' aria="' + redact(aria).slice(0,80) + '"';
            if (label(el)) line += ' label="' + redact(label(el)).slice(0,80) + '"';
            if (tip) line += ' tooltip="' + redact(tip).slice(0,80) + '"';
            if (el.href) line += ' -> ' + redact(el.href).slice(0,200);
            if (el.closest(dialogSelector)) line += ' [dialog]';
            return line;
        };
        const resolve = (query, fieldsOnly = false) => {
            query = String(query).trim();
            if (/^#?\d+$/.test(query)) {
                const key = query.replace(/^#/, '');
                const el = registry.nodes.get(key)?.deref();
                if (!el || !el.isConnected || el.ownerDocument !== document)
                    throw new AutomationError('Stale or unknown element reference; observe the current tab again.');
                if (!visible(el)) throw new AutomationError('The referenced element is no longer visible.');
                return el;
            }
            if (!query) throw new AutomationError('A target is required.');
            const all = collect();
            const candidates = fieldsOnly ? all.filter(el => el.matches(fieldSelector)) : all;
            // A valid CSS locator still observes the same dialog/visibility ordering.
            try {
                for (const root of roots()) for (const el of root.querySelectorAll(query)) {
                    if (visible(el)) { id(el); return el; }
                }
            } catch {}
            const lower = query.toLowerCase();
            for (const exact of [true, false]) for (const el of candidates) {
                if (searchable(el).some(s => exact ? s.toLowerCase() === lower : s.toLowerCase().includes(lower)))
                    return el;
            }
            throw new AutomationError('No matching visible element found.');
        };
        const editable = (el, writing = false) => {
            if (!visible(el)) throw new AutomationError('The element is no longer visible.');
            if (el.disabled || el.matches(':disabled') || (writing && el.readOnly) || el.getAttribute('aria-disabled') === 'true')
                throw new AutomationError('The element is disabled or read-only.');
        };
        const invalid = el => el.getAttribute('aria-invalid') === 'true' || el.validity?.valid === false;
        const validation = el => {
            if (el.type === 'password') return invalid(el) ? 'Password field is invalid.' : '';
            let message = el.validationMessage || '';
            if (!message && el.getAttribute('aria-invalid') === 'true') {
                message = (el.getAttribute('aria-describedby') || '').split(/\s+/)
                    .map(key => document.getElementById(key)?.textContent || '').join(' ');
            }
            return redact(norm(message));
        };
        const nativeSet = (el, property, next) => {
            const prototype = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype :
                el.tagName === 'SELECT' ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
            const setter = Object.getOwnPropertyDescriptor(prototype, property)?.set;
            if (!setter) throw new AutomationError('This element does not support setting a value.');
            const previous = el[property];
            setter.call(el, next);
            el._valueTracker?.setValue(String(previous));
        };
        const validateEdit = edit => {
            const el = edit.node.deref();
            if (!el?.isConnected || el.ownerDocument !== document) throw new AutomationError('The field was replaced during editing; observe it again.');
            if (el[edit.property] !== edit.expected) throw new AutomationError('The field did not retain the requested value.');
            if (edit.checkValidity && invalid(el)) throw new AutomationError(validation(el) || 'The field is invalid.');
        };
        const setValue = (el, next, checkValidity = true) => {
            editable(el, true);
            const text = String(next);
            if (el.type === 'password' && text) registry.secrets.add(text);
            if (el.type === 'file') throw new AutomationError('Use upload for file inputs.');
            el.focus();
            let property = 'value', expected = text;
            if (el.type === 'checkbox' || el.type === 'radio') {
                if (![true,false,'true','false','on','off'].includes(next)) throw new AutomationError('Checkbox/radio value must be true or false.');
                const want = next === true || next === 'true' || next === 'on';
                property = 'checked'; expected = want;
                if (el.checked !== want) el.click();
            } else if (el.tagName === 'SELECT') {
                const options = [...el.options];
                const option = options.find(o => o.value === text || norm(o.text).toLowerCase() === text.toLowerCase())
                    || (text ? options.find(o => norm(o.text).toLowerCase().includes(text.toLowerCase())) : null);
                if (!checkValidity && text === '') {
                    nativeSet(el, 'value', '');
                } else {
                    if (!option || option.disabled || option.parentElement?.disabled) throw new AutomationError('The requested option is missing or disabled.');
                    expected = option.value;
                    nativeSet(el, 'value', option.value);
                }
            } else if (el.isContentEditable) {
                property = 'textContent';
                el.textContent = text;
            } else if (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA') {
                if (['button','submit','reset','image','hidden'].includes(el.type)) throw new AutomationError('The element is not an editable field.');
                nativeSet(el, 'value', text);
            } else throw new AutomationError('The element is not an editable field.');
            if (property !== 'checked') {
                el.dispatchEvent(new InputEvent('input', {bubbles:true, data:text, inputType:'insertText'}));
                el.dispatchEvent(new Event('change', {bubbles:true}));
            }
            el.blur();
            const edit = {node:new WeakRef(el), property, expected, checkValidity};
            registry.edited.push(edit);
            validateEdit(edit);
        };
        try {
            rememberSecrets();
            if (['click','type','clear','select','fill','press'].includes(operation)) registry.changed = performance.now();
            if (['type','clear','select','fill'].includes(operation)) registry.edited = [];
            if (operation === 'validate_edits') {
                for (const edit of registry.edited || []) validateEdit(edit);
                return ok('Edited fields retained their values.');
            }
            if (operation === 'ready') {
                if (document.readyState === 'loading' || !document.body ||
                    [...document.querySelectorAll('[aria-busy="true"],[role="progressbar"]')].some(visible) ||
                    performance.now() - registry.changed < 120)
                    return pending('The page is still loading or updating.');
                return ok('Page ready.');
            }
            if (operation === 'select_option') {
                const dropdown = registry.dropdown, opener = dropdown?.el.deref();
                if (!opener?.isConnected) return fail('The dropdown was removed or the document changed.');
                const containers = (dropdown.controlled || '').split(/\s+/).map(key => document.getElementById(key)).filter(Boolean);
                const all = collect('[role="option"],[role="menuitem"],li[data-value]');
                const options = containers.length ? all.filter(el => containers.some(root => root.contains(el))) : all;
                const matches = el => [norm(el.textContent), norm(el.getAttribute('data-value'))].map(s => s.toLowerCase());
                const lower = value.toLowerCase();
                const option = options.find(el => matches(el).includes(lower)) || options.find(el => matches(el).some(s => s.includes(lower)));
                if (!option) return pending('Waiting for the requested option.');
                editable(option);
                const description = describe(option);
                delete registry.dropdown;
                registry.changed = performance.now();
                option.click();
                return ok('Selected ' + description);
            }
            if (operation === 'look' || operation === 'find') {
                const all = collect();
                let matching;
                if (operation === 'look') {
                    matching = all.filter(el => !target || searchable(el).join(' ').toLowerCase().includes(target.toLowerCase())).slice(0,limit);
                } else {
                    const tokens = norm(target).toLowerCase().split(/\s+/).filter(Boolean);
                    matching = all.map((el, order) => {
                        const parts = searchable(el).map(s => s.toLowerCase());
                        let score = tokens.reduce((sum,t) => sum + (parts.some(p => p === t) ? 50 : parts.some(p => p.includes(t)) ? 12 : 0), 0);
                        if (score > 0 && preferDialog && el.closest(dialogSelector)) score += 2;
                        return {el, order, score};
                    }).filter(it => tokens.length === 0 || it.score > 0)
                        .sort((a,b) => b.score-a.score || a.order-b.order).slice(0,limit).map(it => it.el);
                }
                let output = 'Page: ' + redact(document.title) + '\nURL: ' + redact(location.href) +
                    '\n\n--- Elements ---\n' + (matching.map(describe).join('\n') || '(no matching elements)') +
                    '\n(' + matching.length + ' shown)';
                if (operation === 'look') output += '\n\n--- Text Preview ---\n' + redact(norm(document.body?.innerText)).slice(0,1500);
                return ok(output);
            }
            if (operation === 'read_form') {
                const fields = collect().filter(el => el.matches(fieldSelector));
                const lines = fields.map(el => {
                    const value = el.type === 'password' ? '[redacted]' :
                        el.type === 'checkbox' || el.type === 'radio' ? (el.checked ? 'checked' : 'unchecked') :
                        el.tagName === 'SELECT' ? el.selectedOptions?.[0]?.text || '' :
                        el.isContentEditable ? el.textContent : el.value || '';
                    return describe(el) + ' value="' + redact(value).slice(0,120) + '"' +
                        (el.required || el.getAttribute('aria-required') === 'true' ? ' [required]' : '') +
                        (invalid(el) ? ' [invalid]' : '') + (validation(el) ? ' error="' + validation(el).slice(0,160) + '"' : '');
                });
                return ok('Form fields (' + fields.length + '):\n' + lines.join('\n'));
            }
            if (operation === 'fill') {
                let fields;
                try { fields = JSON.parse(value); } catch { return fail('Invalid JSON for fill.'); }
                if (!fields || Array.isArray(fields) || typeof fields !== 'object' || !Object.keys(fields).length)
                    return fail('Fill requires a nonempty object of field identifiers and values.');
                const entries = Object.entries(fields), results = [];
                for (const [key, next] of entries) {
                    try { if (resolve(key, true).type === 'password' && next) registry.secrets.add(String(next)); } catch {}
                }
                for (let i = 0; i < entries.length; i++) {
                    try {
                        const [key, next] = entries[i];
                        if (next === null || !['string','number','boolean'].includes(typeof next)) throw new AutomationError('Field values must be strings, numbers, or booleans.');
                        const el = resolve(key, true);
                        setValue(el, next);
                        results.push('Filled ' + describe(el) + (el.type === 'password' ? ' value="[redacted]"' : ''));
                    } catch (error) {
                        return fail('Fill stopped at field ' + (i+1) + ': ' + errorText(error) +
                            '\nCompleted fields: ' + results.length + '; unexecuted fields: ' + (entries.length-i-1) +
                            (results.length ? '\n' + results.join('\n') : ''));
                    }
                }
                return ok(results.join('\n'));
            }
            if (operation === 'press') {
                const el = value ? resolve(value) : document.activeElement || document.body;
                if (!el) return fail('No active element.');
                editable(el); el.focus();
                const options = {key:target, code:target, bubbles:true, cancelable:true};
                const proceed = el.dispatchEvent(new KeyboardEvent('keydown', options));
                el.dispatchEvent(new KeyboardEvent('keypress', options));
                el.dispatchEvent(new KeyboardEvent('keyup', options));
                if (proceed && target.toLowerCase() === 'enter' && el.form) el.form.requestSubmit();
                return ok('Pressed key.');
            }
            let el;
            try { el = resolve(target, ['type','clear','select'].includes(operation === 'probe' ? value : operation)); }
            catch (error) {
                if (['wait','probe'].includes(operation) && error instanceof AutomationError && !/^#?\d+$/.test(target.trim()))
                    return pending('Waiting for a matching visible element.');
                throw error;
            }
            if (operation === 'wait') return ok('Element found: ' + describe(el));
            editable(el, operation === 'probe' && ['type','clear'].includes(value));
            if (operation === 'probe') return ok('Target ready.');
            registry.changed = performance.now();
            if (operation === 'click') {
                if (el.type === 'file') return fail('Use upload to attach files without opening a native dialog.');
                const description = describe(el);
                el.focus(); el.click();
                return ok('Clicked ' + description);
            }
            if (operation === 'type' || operation === 'clear') {
                setValue(el, operation === 'clear' ? (el.type === 'checkbox' ? false : '') : value, operation !== 'clear');
                return ok((operation === 'clear' ? 'Cleared ' : 'Typed into ') + describe(el) +
                    (el.type === 'password' ? ' value="[redacted]"' : ''));
            }
            if (operation === 'select') {
                if (el.tagName === 'SELECT') { setValue(el,value); return ok('Selected option in ' + describe(el)); }
                if (el.getAttribute('role') !== 'combobox' && el.getAttribute('aria-haspopup') !== 'listbox')
                    return fail('Target is not a select or custom combobox.');
                const controlled = el.getAttribute('aria-controls') || el.getAttribute('aria-owns') || '';
                registry.dropdown = {el:new WeakRef(el), controlled};
                el.focus(); el.click();
                return pending('Dropdown opened; waiting for the requested option.');
            }
            return fail('Unsupported DOM operation.');
        } catch (error) {
            return fail(errorText(error));
        }
        """;
}
