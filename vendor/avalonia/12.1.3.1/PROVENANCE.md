# Avalonia 12.1.3.1 - temporary stable-base backports

This is an unofficial modified `Avalonia` package, not an official Avalonia
release. It refreshes Lumi's temporary fork onto stable 12.1.3 and includes the
final implementations accepted upstream for both crash fixes.

## Pinned inputs

- Official tag: `12.1.3`
- Source commit: `8eeda4f6f546165b3f72e63c9f42247abb306905`
- Original package:
  `https://api.nuget.org/v3-flatcontainer/avalonia/12.1.3/avalonia.12.1.3.nupkg`
- Original package SHA-256:
  `dffb6605b02e144866cb23765eb5af7255379ebb7ede75cf11066f3248d83734`
- The official nuspec records the same source commit.
- Build SDK: exactly `10.0.201`, from upstream `global.json`.
- Framework assets: `net8.0` and `net10.0`.
- Assembly identity remains `12.1.3.0`, public-key token `c8d484a7012f9a8b`;
  the build uses upstream's distributed `build/avalonia.snk`.
- File version: `12.1.3.1`; informational version:
  `12.1.3.1-lumi-text-contract-fixes` plus the source revision.

## Accepted fixes

1. [AvaloniaUI/Avalonia#22343](https://github.com/AvaloniaUI/Avalonia/pull/22343),
   accepted commit `1b50ee4bdd5e39d893f00ec52cb56efb35bb6212`:
   batch selection notifications during `TextBox` text changes and inside the
   input-method client's `Selection` setter, removing its duplicate notification.
2. [AvaloniaUI/Avalonia#22344](https://github.com/AvaloniaUI/Avalonia/pull/22344),
   accepted commit `11ea9b1120f1ef2a8bbf1a513c9e35520eb7f675`:
   return an empty bounds collection for zero-length `TextLine.GetTextBounds`
   requests, with the corresponding API documentation update.

The included source/test patches are exact diffs of those accepted commits,
applied to the pinned stable source. This revision does not retain the earlier
layout-only guards. The included regression tests exercise ordinary input-method
notification and direct text-line APIs; the reporter's exact native
`SelectableTextBlock` interaction sequence remains unknown.

## Construction and compatibility

`Rebuild.ps1` applies the two patches, builds Base and Controls in Release for
both framework assets, runs the TextBox and TextLine/TextLayout suites, and
compares their assembly identities, keys, definitions and references against
the official binaries. It also verifies both compiled batching scopes and the
compiled empty-bounds return.

The packer replaces only Base/Controls runtime DLLs and includes their PDBs with
embedded source. Every official restricted `ref/` entry, unchanged runtime
assembly, analyzer and tool is retained byte-for-byte. Dependency groups are
unchanged. Only local package/version metadata and content types are updated.
`lumi-patch/payload-manifest.json` records the exact changed/removed entries,
source/patch commits and payload hashes.

The modified NuGet is unsigned. Its invalidated original NuGet signature and
stale CycloneDX BOM are removed; no original package signature is claimed for
the changed bytes. Original license and third-party notices are included.

## Consumption and retirement

Lumi pins only the core to `12.1.3.1`; official peer packages use `12.1.3`.
The source mapping remains exact-ID `Avalonia`, not `Avalonia.*`. Old package
versions and their reproduction inputs remain immutable.

Both PRs are merged, but stable 12.1.3 predates them. Remove the temporary fork
only after an official stable tag/source and NuGet binaries demonstrably contain
both fixes and pass both crash regressions plus Lumi's app validation.
