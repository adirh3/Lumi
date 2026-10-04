# Avalonia 12.1.2.2 — local TextBox and TextLayout contract fixes

This is an **unofficial, local-only modified Avalonia package**, not an upstream
release. Do not publish it to nuget.org or any public feed.

## Source and original input

- Repository: https://github.com/AvaloniaUI/Avalonia
- Release tag: `12.1.2`
- Exact commit: `d3c867a9e2de379249b03dbeb3495bd7f076a81a`
- Required XamlX submodule: `d7e37ca63dc9b13cdc95ca165938d4904fa0eddf`
  (`https://github.com/kekekeks/XamlX.git`)
- The Avalonia.DBus submodule is not needed by this source build and is not built.
- Original package:
  `https://api.nuget.org/v3-flatcontainer/avalonia/12.1.2/avalonia.12.1.2.nupkg`
- Original package SHA-256:
  `99987414c63ac3993346a84a852006df963ff839f96d140557ee31f8850db08f`
- The original nuspec records the same upstream commit.
- SDK: .NET SDK `10.0.201`, as required by upstream `global.json`;
  SDK commit `4d3023de60`, MSBuild `18.3.0-release-26153-122`.
- Original build host: Windows x64; Python `3.11.9` for deterministic ZIP assembly.
- Git checkout uses `core.autocrlf=true` and repository-local
  `core.longpaths=true`; the build script pins both settings.
- Strong-name key: the **publicly distributed upstream** `build/avalonia.snk`,
  SHA-256 `e11e9cc0585a0e0013a666be2c277b161179429f0ceb20bb4141768795ea8e91`.

## Exact production changes

`patches/0001-textbox-selection-notifications.patch` contains the production
change and seven focused upstream regression cases.

At the beginning of the `TextProperty` branch of `TextBox.OnPropertyChanged`,
an existing `_imClient.BeginChange()` scope now spans the whole branch. It batches
native selection notifications until text-triggered caret/selection coercion,
text events, pseudoclasses, and command state updates finish.

The implementation does not skip or replace coercion, text input, undo/redo,
property handling, template behavior, or the native input client. There are no
reflection calls, detours, runtime IL modifications, or Lumi-specific production
hooks. These are managed, platform-neutral framework source changes.

`patches/0002-textlayout-empty-ranges.patch` adds exactly two `length == 0`
conditions in `TextLayout.HitTestTextRange`:

- Return empty geometry immediately when the requested length is zero.
- Break line traversal when the remaining length becomes zero, before another
  line can receive a zero-length `GetTextBounds` call.

No negative-range check is broadened. `TextLineImpl` is unchanged, exceptions
are not swallowed, and aggregation/progress semantics are otherwise unchanged.
Six focused tests use actual `TextLayout` and the upstream Skia/font services.

### Second-report evidence boundary

The confirmed direct API reproduction is
`new TextLayout("a\r\nb\r\n", ...).HitTestTextRange(3, 0)`, which throws
`ArgumentOutOfRangeException` with `ParamName="textLength"` and `ActualValue=0`
in unpatched `TextLineImpl.GetTextBounds`. An interior empty range on `"abc"`
also reproduces it.

This is **not a reproduction of the reporter's original SelectableTextBlock
rendering crash**. `RenderTextLayout` normally skips collapsed selections.
Original native actions and content are unavailable. Parent owns the UI
investigation and integration tests. No claim is made that the positive-range
exhaustion route was independently reproduced; its explicit stop is the
requested callee-contract safeguard.

## Smallest coherent package strategy

Keep package ID **`Avalonia`** and give the changed bytes a distinct numeric
version **`12.1.2.2`**. NuGet treats it as greater than `12.1.2`, unlike a
`12.1.2-...` prerelease. This is not a build-metadata-only alias.

The earlier `Avalonia.12.1.2.1.nupkg` remains byte-for-byte intact, with
SHA-256 `8460a9ebda3c96769f20b90f26d542be1693735c75c45660af57a813c79489a2`.
Its build inputs were preserved in `revisions/12.1.2.1-build-inputs.zip`.

The official 12.1.2 Desktop, SimpleTheme, Skia, Win32, Native, and X11 nuspecs
depend on `Avalonia` with `version="12.1.2"`: a minimum-version range, not
`[12.1.2]`. DataGrid 12.1.2 similarly requires a minimum of 12.1.0.
They can therefore remain on **official 12.1.2**. Keep
`Avalonia.Remote.Protocol` at official **12.1.2** too. No family-wide bump is
required.

The `Avalonia` package is an upstream merged package containing several framework
assemblies, tooling, analyzers, build assets, and restricted reference assemblies.
Rather than rebuilding/repacking unrelated tooling and peers, this artifact:

1. Hash-verifies the exact original 12.1.2 package.
2. Builds `src/Avalonia.Controls/Avalonia.Controls.csproj` and its Base project
   dependency from patched upstream source in Release for **both `net8.0` and
   `net10.0`**.
3. Replaces only `Avalonia.Base.dll` and `Avalonia.Controls.dll` under each
   `lib/<tfm>/` directory during **offline package construction**.
4. Preserves every unchanged assembly, analyzer, build asset, and restricted
   `ref/` entry from the official package byte-for-byte.
5. Includes Base/Controls PDBs with embedded source, both exact patches, this provenance,
   license/third-party notices, and a payload hash manifest.
6. Updates the package version, package description, package core metadata, and
   `build/AvaloniaVersion.props`. Removes the now-invalid original NuGet signature
   and stale original CycloneDX BOM. It does not claim the modified package is
   signed or that the original BOM covers the modified payload.

This is **not application post-build DLL copying**: consumers restore the new
NuGet identity normally, and no application binaries are modified after build.
Reference assemblies remain unchanged because the patch changes no API or
metadata contract. Upstream's restricted-ref behavior is deliberately preserved.

All four rebuilt Base/Controls DLLs retain:

- Assembly names: `Avalonia.Base` and `Avalonia.Controls`
- Assembly version: **`12.1.2.0`**
- Public-key token: **`c8d484a7012f9a8b`**
- File version: `12.1.2.2`
- Informational version:
  `12.1.2.2-lumi-text-contract-fixes+d3c867a9e2de379249b03dbeb3495bd7f076a81a`

## Build commands

From the pinned and patched source checkout, using the pinned SDK:

```powershell
$properties = @(
  '-p:ContinuousIntegrationBuild=true', '-p:NuGetAudit=false',
  '-p:AssemblyVersion=12.1.2.0', '-p:FileVersion=12.1.2.2',
  '-p:InformationalVersion=12.1.2.2-lumi-text-contract-fixes',
  '-p:EmbedAllSources=true'
)
dotnet build src/Avalonia.Controls/Avalonia.Controls.csproj -c Release @properties

dotnet test --project tests/Avalonia.Controls.UnitTests/Avalonia.Controls.UnitTests.csproj `
  -c Release --filter-class 'Avalonia.Controls.UnitTests.TextBox*' `
  --report-trx --results-directory ../verification/combined-textbox @properties

dotnet test --project tests/Avalonia.Skia.UnitTests/Avalonia.Skia.UnitTests.csproj `
  -c Release --filter-class 'Avalonia.Skia.UnitTests.Media.TextFormatting.TextLayout*' `
  --report-trx --results-directory ../verification/combined-textlayout @properties
```

`Rebuild.ps1` automates isolated source acquisition, exact-commit checking, patch
application, manifested restore/build/test, compatibility verification, and
offline package assembly. It never accesses or changes Lumi or Strata.
The local NuGet cache and .NET CLI home are under its root.

## Verification and limits

- Before applying the production fix, the seven new tests produced **4 failures,
  3 passes**. The forward/reverse notifications exposed `5..18` and `5..8`.
  Reentrant `TextInput` produced the exact `ArgumentOutOfRangeException` with
  parameter `length` at `StringBuilder.Remove -> TextBox.HandleTextInput`.
- After the source fix: **198/198 existing and new TextBox tests passed**.
- Before the Base fix, the final six geometry tests produced **2 failures,
  4 passes**: the two empty-range cases above failed at the exact nonzero-length
  contract. Existing negative-range behavior and completed-line geometry passed.
- With the Base fix, all six new tests pass. The complete upstream `TextLayout*`
  set passes: **79 passed, 1 existing profiling-only test skipped**.
- Both shipped TFMs build in Release with **0 warnings and 0 errors**.
- All four rebuilt DLLs passed the Windows SDK strong-name utility:
  `sn.exe -vf <dll>`. This verifies their signatures, not merely their tokens.
- Metadata comparison against official Controls found **23,875** unchanged
  definition/reference records for net8.0 and **23,873** for net10.0, including
  internal definitions used by official peers. The new compiled `BeginChange`
  call and `finally` scope were checked in `OnPropertyChanged`.
- Metadata comparison against official Base found **44,042** unchanged
  definition/reference records for net8.0 and **44,052** for net10.0.
  Both source-compiled zero-length checks were verified in `HitTestTextRange`.
- Partial selections that remain valid are unchanged and produce no spurious
  selection notification.
- No Lumi repository tests, Strata source, or existing app processes were touched.
  Second-report UI investigation and Lumi integration remain parent-owned.
- Managed source and TFMs are portable; this task does not claim native
  macOS/Linux/Windows UI-driver coverage.
- The official NuGet signature is not retained. Strong-name assembly identity is
  separate from NuGet repository/package signing.
- SourceLink still identifies the base upstream commit; embedded Base/Controls
  source in the PDBs and the included patches identify the actual modified source.

`consumer/` is a separate package-only smoke test, not a Lumi integration change.
It pins the patched core and official Desktop, Headless, SimpleTheme, and
DataGrid 12.1.2 peers with exact package-ID source mapping. It compiles against
the retained official reference assemblies and checks seven stock
TextBox/SimpleTheme cases plus six direct TextLayout contract cases at runtime.
Use `dotnet build consumer/Consumer.csproj
-c Release` and run each `consumer/bin/Release/<tfm>/Consumer.dll` with a host
containing that runtime. The locally installed SDK includes runtime 10.0.5;
net8.0 execution needs an existing .NET 8 runtime.

## Parent integration (not performed here)

Vendor the `.nupkg` and this build/patch provenance into the existing local feed.
Add exact source mapping `Avalonia` to `lumi-vendored` (do not map `Avalonia.*`).
Use a separate core package-version property, e.g. `AvaloniaCoreVersion=12.1.2.2`,
for every direct **`Avalonia`** reference across the consuming project graph,
while leaving `AvaloniaVersion=12.1.2` for official peers.

Verify the resolved `project.assets.json` files, including Strata's net8.0 target,
and the real Lumi regression tests. Do not overwrite an already-restored package
identity with different bytes. A subsequent additional patch needs a new numeric
revision, e.g. `12.1.2.3`.
