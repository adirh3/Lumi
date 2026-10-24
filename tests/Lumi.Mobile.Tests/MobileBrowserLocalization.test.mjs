import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const webSource = name => readFileSync(
    new URL(`../../src/Lumi.Mobile.Browser/wwwroot/${name}`, import.meta.url), 'utf8');
const resourceSource = language => readFileSync(new URL(
    `../../src/Lumi.Mobile/Localization/ChatSessionStrings${language === 'he' ? '.he' : ''}.resx`,
    import.meta.url), 'utf8');
const decode = value => value.replace(/&lt;/g, '<').replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, '&');
const readCatalog = xml => new Map([...xml.matchAll(
    /<data name="([^"]+)"[^>]*>\s*<value>([\s\S]*?)<\/value>\s*<\/data>/g)]
    .map(match => [match[1], decode(match[2])]));

async function page({ savedLanguage, browserLanguage = 'en-US', blockedStorage = false, failedFetch = false } = {}) {
    const nodes = [
        { dataset: { lumiString: 'WebOpening' }, textContent: 'Opening Lumi...' },
        { dataset: { lumiString: 'TryAgain' }, textContent: 'Try again' },
        { dataset: { lumiString: 'AndroidInstallTitle' }, textContent: 'Install Lumi for Android' }
    ];
    const manifest = { href: './manifest.webmanifest', setAttribute(_name, value) { this.href = value; } };
    const document = {
        baseURI: 'https://lumi.test/app/index.html', documentElement: {},
        querySelectorAll: () => nodes,
        querySelector: () => manifest
    };
    const requests = [];
    const context = vm.createContext({
        URL, AbortController, setTimeout, clearTimeout, document,
        navigator: { languages: [browserLanguage], language: browserLanguage },
        get localStorage() {
            if (blockedStorage)
                throw new Error('Storage blocked');
            return {
                getItem(key) {
                    assert.equal(key, 'lumi.mobile.connection.v1');
                    return savedLanguage === undefined ? null
                        : JSON.stringify({ language: savedLanguage, token: 'never-exposed' });
                }
            };
        },
        async fetch(url, options) {
            requests.push({ url: url.href, options });
            if (failedFetch)
                throw new Error('Offline');
            const language = url.pathname.endsWith('/he.xml') ? 'he' : 'en';
            return { ok: true, text: async () => resourceSource(language) };
        },
        DOMParser: class {
            parseFromString(xml) {
                const entries = [...readCatalog(xml)].map(([key, value]) => ({
                    getAttribute: () => key, querySelector: () => ({ textContent: value })
                }));
                return { querySelector: () => null, querySelectorAll: () => entries };
            }
        }
    });
    vm.runInContext(webSource('localization.js'), context);
    await context.lumiLocalization.ready;
    return { context, localization: context.lumiLocalization, document, nodes, manifest, requests };
}

test('saved Hebrew is applied to startup, installation and manifest without changing saved connection data', async () => {
    const app = await page({ savedLanguage: 'he', browserLanguage: 'en-US' });
    assert.equal(app.localization.language, 'he');
    assert.equal(app.document.documentElement.lang, 'he');
    assert.equal(app.document.documentElement.dir, 'rtl');
    assert.equal(app.nodes[0].textContent, 'פותח את Lumi...');
    assert.equal(app.nodes[1].textContent, 'לנסות שוב');
    assert.equal(app.nodes[2].textContent, 'התקנת Lumi ל־Android');
    assert.equal(app.manifest.href, './manifest.he.webmanifest');
    assert.equal(app.requests[0].url, 'https://lumi.test/app/localization/he.xml');
    assert.equal(app.requests[0].options.credentials, 'same-origin');
    assert.equal(JSON.stringify(app.localization).includes('never-exposed'), false);
});

test('explicit English overrides the Hebrew browser and retains English fallback wording', async () => {
    const app = await page({ savedLanguage: 'en', browserLanguage: 'he-IL' });
    assert.equal(app.localization.language, 'en');
    assert.equal(app.document.documentElement.dir, 'ltr');
    assert.equal(app.nodes[0].textContent, 'Opening Lumi...');
    assert.equal(app.manifest.href, './manifest.webmanifest');
    assert.equal(app.localization.text('UnknownKey', 'English fallback'), 'English fallback');
    assert.equal(app.localization.text('WebStartFailed', '', '<unexpected response>'),
        'Lumi could not start: <unexpected response>');
});

test('first run and blocked browser storage follow the browser language', async () => {
    for (const blockedStorage of [false, true]) {
        const app = await page({ browserLanguage: 'he-IL', blockedStorage });
        assert.equal(app.localization.language, 'he');
        assert.equal(app.requests[0].url, 'https://lumi.test/app/localization/he.xml');
    }
    assert.equal((await page({ browserLanguage: 'fr-FR' })).localization.language, 'en');
});

test('offline first-load failure leaves usable English fallbacks and never blocks startup', async () => {
    const app = await page({ savedLanguage: 'he', failedFetch: true });
    assert.equal(app.nodes[0].textContent, 'Opening Lumi...');
    assert.equal(app.localization.text('WebOffline', 'offline fallback'), 'offline fallback');
    assert.equal(app.requests.length, 1);
});

test('pre-runtime pages use matching resx keys and format arguments', () => {
    const english = readCatalog(resourceSource('en'));
    const hebrew = readCatalog(resourceSource('he'));
    assert.deepEqual([...english.keys()].sort(), [...hebrew.keys()].sort());
    for (const [key, value] of english) {
        const argumentsOf = text => [...text.matchAll(/\{(\d+)(?:[^{}]*)\}/g)]
            .map(match => match[1]).sort();
        assert.ok(value.trim(), key);
        assert.ok(hebrew.get(key).trim(), key);
        assert.deepEqual(argumentsOf(value), argumentsOf(hebrew.get(key)), key);
    }
    for (const name of ['index.html', 'android.html', 'cache-recovery.html']) {
        const html = webSource(name);
        assert.match(html, /src="\.\/localization\.js"/);
        for (const [, key] of html.matchAll(/data-lumi-string="([^"]+)"/g))
            assert.ok(english.has(key), `${name}: ${key}`);
    }
    for (const name of ['main.js', 'browserHost.js', 'cache-recovery.js', 'workerFrames.js']) {
        for (const [, key] of webSource(name).matchAll(/uiText\('([^']+)'/g))
            assert.ok(english.has(key), `${name}: ${key}`);
    }
    assert.equal(JSON.parse(webSource('manifest.webmanifest')).description, english.get('WebManifestDescription'));
});
