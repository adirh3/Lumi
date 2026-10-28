// The pre-runtime pages read the same resx catalogs that ResourceManager uses in the mobile app.
// Publishing copies them as XML; the service worker includes them in its verified offline assets.
(() => {
    const supportedLanguage = value => {
        const code = (value || '').trim().toLowerCase();
        if (/^he(?:-|$)/.test(code)) return 'he';
        const base = code.split('-')[0];
        if (['de', 'fr', 'ja', 'ko', 'ar'].includes(base)) return base;
        if (/^es(?:-|$)/.test(code)) return 'es';
        if (/^pt(?:-|$)/.test(code)) return 'pt-BR';
        if (/^zh(?:-hans(?:-|$)|-cn$|-sg$|$)/.test(code)) return 'zh-Hans';
        return 'en';
    };
    let language;
    try {
        const saved = JSON.parse(globalThis.localStorage?.getItem('lumi.mobile.connection.v1') || 'null');
        if (typeof saved?.language === 'string' && saved.language.trim())
            language = supportedLanguage(saved.language);
    } catch {
        // Browser privacy settings must not prevent startup or the Android installation page.
    }
    language ??= supportedLanguage(navigator.languages?.[0] || navigator.language);
    let strings = new Map();
    const text = (key, fallback, ...args) => {
        const template = strings.get(key);
        return template === undefined ? fallback
            : template.replace(/\{(\d+)\}/g, (match, index) => String(args[Number(index)] ?? match));
    };
    document.documentElement.lang = language;
    document.documentElement.dir = language === 'he' || language === 'ar' ? 'rtl' : 'ltr';

    const ready = (async () => {
        const cancellation = new AbortController();
        const timeout = setTimeout(() => cancellation.abort(), 3000);
        try {
            const response = await fetch(new URL(`./localization/${language}.xml`, document.baseURI), {
                credentials: 'same-origin',
                signal: cancellation.signal
            });
            if (!response.ok)
                return;
            const catalog = new DOMParser().parseFromString(await response.text(), 'application/xml');
            if (catalog.querySelector('parsererror'))
                return;
            strings = new Map([...catalog.querySelectorAll('data')].map(entry => [
                entry.getAttribute('name'), entry.querySelector('value')?.textContent ?? ''
            ]));
            for (const element of document.querySelectorAll('[data-lumi-string]'))
                element.textContent = text(element.dataset.lumiString, element.textContent);
            const manifest = document.querySelector('link[rel="manifest"]');
            if (manifest && language !== 'en')
                manifest.setAttribute('href', `./manifest.${language}.webmanifest`);
        } catch {
            // The HTML and call sites retain English fallbacks if the first network load fails.
        } finally {
            clearTimeout(timeout);
        }
    })();

    globalThis.lumiLocalization = { language, ready, text };
})();
