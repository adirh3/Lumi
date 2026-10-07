const parameters = new URLSearchParams(globalThis.location.search);
const version = parameters.get('version')?.trim() || '';

const validVersion = /^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$/.test(version);
const apkUrl = validVersion
    ? `https://github.com/adirh3/Lumi/releases/download/v${encodeURIComponent(version)}/Lumi-${encodeURIComponent(version)}-android-arm64.apk`
    : 'https://github.com/adirh3/Lumi/releases/latest';

const download = document.querySelector('#download-apk');
download.href = apkUrl;

const versionLabel = document.querySelector('#version-label');
if (validVersion)
    versionLabel.textContent = `Lumi ${version}, matching this desktop release.`;

const openLumi = document.querySelector('#open-lumi');
const server = globalThis.location.origin;
openLumi.href =
    `intent://connect?server=${encodeURIComponent(server)}` +
    '#Intent;scheme=lumi;package=com.lumi.mobile;' +
    `S.browser_fallback_url=${encodeURIComponent(apkUrl)};end`;
