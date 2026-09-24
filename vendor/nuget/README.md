# Vendored Copilot SDK package

`Lumi.Copilot.SDK` is an **unofficial, temporary prerelease build** of the GitHub
Copilot .NET SDK with the native in-memory skill provider proposed upstream and
package-owned full CLI acquisition. It is not an official GitHub release.

## Provenance

- Package: `Lumi.Copilot.SDK` version `1.0.14-preview.1.lumi.2`
- Source: [adirh3/copilot-sdk at 4a33b2c](https://github.com/adirh3/copilot-sdk/tree/4a33b2cf5ff7993a58d50aa80a8be008660bf0b7)
- Exact commit: `4a33b2cf5ff7993a58d50aa80a8be008660bf0b7`
- Upstream PR: [github/copilot-sdk#2672](https://github.com/github/copilot-sdk/pull/2672)
- Related issue: [github/copilot-sdk#2651](https://github.com/github/copilot-sdk/issues/2651)
- Official full CLI selected by the package: `1.0.84-8`
- SDK targets: `net8.0`, `net10.0`, `netstandard2.0`
- License: MIT; upstream license and attribution are included in the package.
- SHA-256 of `Lumi.Copilot.SDK.1.0.14-preview.1.lumi.2.nupkg`:
  `179ee8bb880f60e95c262c0b75ffa0981444f039edb95872a9b9fe3ed8e25df6`

All managed assemblies and XML documentation are byte-for-byte identical to
`.lumi.1`. The assembly remains `GitHub.Copilot.SDK`; do not reference this package
and the official SDK package together. `.lumi.2` replaces only package build assets
and metadata; it does not alter SDK authentication or skill-provider code.

## Consuming and updating

Normal `dotnet restore`, `dotnet build`, and publish use this feed and the package's
build targets automatically. No separate Lumi acquisition task, import, or opt-out
flag is needed. The package verifies official archive and executable SHA-256 pins
for eight portable RIDs and copies one `copilot[.exe]` plus `.copilot-explicit-cli`
to `runtimes/{rid}/native`. The existing SDK recognizes that marker for default
stdio startup. Old headless payloads are removed on incremental builds.

The default is full-CLI stdio/TCP hosting, not FFI/in-process hosting. The public
`CopilotSkipCliDownload` and `CopilotCliBinaryPath` overrides remain available.
The CLI's own extraction-cache and storage behavior is unchanged; no second
runtime or separate Node.js installation is shipped.

## Reproducing the package

The package contains all replacement sources in `build/FullCli/`, its
`build/Lumi.Copilot.SDK.targets`, `tools/repack.py`, and focused package tests.
This avoids putting dependency acquisition code in the Lumi application.

1. Extract `.lumi.2` into a temporary directory.
2. Run `python tools\repack.py <original-.lumi.1.nupkg> <new-output.nupkg>` there.
3. Verify the output SHA-256 matches the `.lumi.2` hash above. The script preserves
   every original `lib/` entry and refuses to overwrite an existing package.
4. Run `tools\tests\CopilotPackaging.Tests.csproj` with .NET 11 and a NuGet source
   containing `.lumi.2`; these are package-maintainer tests, not application tooling.

The immutable input `.lumi.1` remains in this feed with SHA-256
`edfdcec2af7e7561274f5e5bbc3e9bc534cc81e00c78934ec074df0ac3a23a87`.
Python is needed only for reproduction, never for normal builds. Future changes
must produce a new package version, with reviewed CLI pins and updated provenance;
do not replace the bytes of an already-used version.

The upstream API is experimental and currently text-only/pathless. Once an official
SDK release provides the required API, restore the official package reference and
remove this package and its local-feed mapping.
