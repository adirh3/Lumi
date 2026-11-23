# Vendored NuGet packages

## Avalonia dependency

Lumi consumes official `Avalonia 12.2.0` from NuGet.org. The stable packages
include both text fixes accepted in 12.1.4:
[AvaloniaUI/Avalonia#22343](https://github.com/AvaloniaUI/Avalonia/pull/22343) and
[AvaloniaUI/Avalonia#22344](https://github.com/AvaloniaUI/Avalonia/pull/22344).
The temporary Avalonia packages, rebuild inputs, checksum rows and exact local
source mapping have been removed after verifying the official binaries against
the input and direct empty-bounds regressions.

`Avalonia.Controls.DataGrid` remains independently pinned to published `12.1.2`.
The Copilot SDK archives below are unchanged and are not active dependencies.

## Archived Copilot SDK packages

Lumi now references official `Microsoft.Copilot.SDK 2.0.1` from NuGet.org.
The packages below are retained only as historical provenance: they are not
referenced or source-mapped by Lumi.
This directory is retained as an archive, not an active package feed.
`build/Copilot/CopilotCli.targets` supplies the full CLI for both protocol-4
runtime hosting and sign-in via the SDK's binary-path override. See
[development.md](../../docs/development.md) for the current layout.

`Lumi.Copilot.SDK` is an **unofficial, temporary prerelease build** of the GitHub
Copilot .NET SDK with the native in-memory skill provider proposed upstream and
package-owned full CLI acquisition. It is not an official GitHub release.

### Provenance

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

### Historical consumption and updating

When this package was active, restore, build and publish used its feed and targets.
It verified official archive and executable SHA-256 pins for eight portable RIDs
and copied one `copilot[.exe]` plus `.copilot-explicit-cli` to
`runtimes/{rid}/native`. The existing SDK recognized that marker for default
stdio startup. Old headless payloads were removed on incremental builds.

That archived package used full-CLI stdio/TCP hosting, not FFI/in-process
hosting. Current builds use the official SDK's `CopilotCliBinaryPath` override
with a full CLI that supports protocol 4.

### Reproducing the package

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

The upstream API remains experimental and text-only/pathless. Lumi's active SDK
reference and provider integration no longer depend on these packages or their
custom SDK implementation.
