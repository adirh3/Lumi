# Vendored Avalonia 12.1.2.2

Vendored, unofficial package based on upstream Avalonia 12.1.2, commit
`d3c867a9e2de379249b03dbeb3495bd7f076a81a`.

Package: `../../nuget/Avalonia.12.1.2.2.nupkg`

SHA-256: `226151f857f14819d1a4d7132c74b8b79457e41eae32547b642fcfbe8f3d4b2b`

## Upstream tracking and temporary fork

- [AvaloniaUI/Avalonia#22343](https://github.com/AvaloniaUI/Avalonia/pull/22343):
  batch TextBox selection notifications during text replacement.
- [AvaloniaUI/Avalonia#22344](https://github.com/AvaloniaUI/Avalonia/pull/22344):
  handle empty ranges in TextLayout hit testing.
- The combined source backport is published in `adirh3/Avalonia` on
  `lumi/12.1.2-text-fixes`, pinned at
  [24fe5a4964f405490e76e97afb2fea28ace62cde](https://github.com/adirh3/Avalonia/tree/24fe5a4964f405490e76e97afb2fea28ace62cde).
  Its changed source and tests match the patches used for this package.

The PRs independently target upstream `main`; the package remains based on
12.1.2. Publishing the source branches does not change the package or its hash.
Once an official release contains the fixes, remove the local core-version pin
and exact-ID source mapping after validating the official dependency.

## Contents and preservation

- `patches/0001-textbox-selection-notifications.patch`: existing IM-client
  `BeginChange()` scope around TextBox's complete text-change branch, with
  upstream regression tests.
- `patches/0002-textlayout-empty-ranges.patch`: two zero-length guards in
  `TextLayout.HitTestTextRange`, with real upstream Skia/TextLayout tests.
- `Rebuild.ps1` plus two small tools: source-first build, tests, read-only
  compatibility checking, and offline NuGet construction.
- `licence.md` and `NOTICE.md`: original upstream license and third-party notices.
- `PROVENANCE.md`: full source/build/verification record. Its references to
  `consumer/`, earlier revisions, and TRX reports describe evidence retained in
  the original isolated build workspace; those build outputs are not vendored.

The package contains source-built **Avalonia.Base and Avalonia.Controls** for
**net8.0 and net10.0**. Both retain assembly version `12.1.2.0` and public-key
token `c8d484a7012f9a8b`; file versions identify the local revision `12.1.2.2`.

All **44 restricted reference entries** are byte-for-byte official. All **25
tool/analyzer/build entries** other than the intentional package-version props
update are unchanged. Both original NuGet dependency groups are unchanged.
Original license/notices are also included inside the package. No runtime
reflection hook, IL rewrite, private API use by Lumi, or app post-build DLL
copying is needed.

The modified NuGet package is unsigned; the invalidated original NuGet signature
and stale original BOM were removed. The four rebuilt assemblies' strong-name
signatures were independently verified.

## Integration example — parent-owned

Copy `vendor-artifacts/nuget/` to the existing `vendor/nuget/` feed and this
directory to `vendor/avalonia/12.1.2.2/`. No Strata files need modification.

`integration/Directory.Build.targets.example` is a non-active example for a
small root `Directory.Build.targets`. It defines the core version there and
updates only existing **Avalonia** package references after project items.
Standard SDK `.targets` discovery reaches the root from Strata because Strata
has no nearer `Directory.Build.targets`; its separate `.props` does not block
that lookup. Keep both existing `AvaloniaVersion` properties at **12.1.2** so
Desktop, SimpleTheme, Headless, Skia, DataGrid, and other peers remain official.

Add only `<package pattern="Avalonia" />` to the existing `lumi-vendored` source
mapping, preserving other mappings. Do not map `Avalonia.*` to the local feed.
Verify actual resolved package references for the root and Strata target graphs.

Use a fresh isolated `--artifacts-path` for parent-owned builds/tests to avoid
existing app locks and stale copied assemblies. When reusing an output directory,
perform a clean rebuild (`-t:Rebuild`): deterministic package timestamps plus
unchanged file sizes allowed an incremental copy to retain an older Controls
DLL in the initial consumer run. The final clean runs verified every Base and
Controls output hash against this package. Do not terminate the running app.

## Reproduce without writing build outputs into the repository

Prerequisites: Git, GitHub CLI, PowerShell 7, Python 3.11.9, .NET SDK **10.0.201**.
Install that SDK system-wide or under `<workRoot>/dotnet` using Microsoft's
official installer. The original authorized build workspace already contains it.

```powershell
$inputs = 'E:/path/to/repo/vendor/avalonia/12.1.2.2'
$workRoot = 'C:/path/to/dedicated/avalonia-fix-build'
& "$inputs/Rebuild.ps1" -WorkRoot $workRoot
Get-FileHash "$workRoot/feed/Avalonia.12.1.2.2.nupkg" -Algorithm SHA256
```

The script pins the exact source commit, original package hash, source patches,
SDK, assembly identity, and compilation flags. It restores only declared upstream
dependencies. NuGet/CLI caches and all compilation outputs stay under `workRoot`.
Official input bytes are downloaded and hash-checked rather than duplicated here.
The packer refuses different bytes at an already-existing package version.

Validated: **198 TextBox tests passed; 79 TextLayout tests passed and one existing
profiling-only test skipped; 13 package-consumer cases passed on each of .NET 8
and .NET 10**. Parent owns the real Lumi tests and UI integration.

The second fix proves the direct empty-range API contract, including the supplied
CRLF example. It does **not** claim to reproduce the reported SelectableTextBlock
rendering crash; original actions/content are unavailable and rendering normally
skips collapsed selections.

The package is not published to a public NuGet feed. Further source changes
require a new numeric package revision.
