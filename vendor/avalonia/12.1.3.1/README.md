# Vendored Avalonia 12.1.3.1

Unofficial core package based on official Avalonia **12.1.3**, source commit
`8eeda4f6f546165b3f72e63c9f42247abb306905`.

Package: `../../nuget/Avalonia.12.1.3.1.nupkg`

SHA-256: `9e77f8fc7234639cceb2abdace3d8f04d04410c072b61b6dd054bb5d6fb5eeb6`

## Why this revision exists

It picks up 12.1.3's stable fixes while retaining the final merged changes from
[AvaloniaUI/Avalonia#22343](https://github.com/AvaloniaUI/Avalonia/pull/22343)
and [AvaloniaUI/Avalonia#22344](https://github.com/AvaloniaUI/Avalonia/pull/22344).
The TextBox fix covers both text replacement and client selection assignment;
empty ranges are handled directly by `TextLine.GetTextBounds`.

Only Base/Controls runtime assemblies are rebuilt for **net8.0 and net10.0**.
Official reference assemblies, unchanged runtime assemblies, tools and analyzers
are preserved. `PROVENANCE.md` and the packaged payload manifest document the
input pins, accepted source commits and output contracts.

The unpatched source failed eight TextBox notification tests and ten text-range
tests, including the reentrant `StringBuilder.Remove` crash. After the backports,
all **203 TextBox tests** and **168 TextLine/TextLayout tests** pass, with one
existing profiling-only skip. Both framework assets preserve the official
assembly definition/reference contracts.

## Lumi integration

- `AvaloniaCoreVersion` in root `Directory.Build.targets`: **12.1.3.1**.
- `AvaloniaVersion` in root and Strata `Directory.Build.props`: **12.1.3**.
- Keep the existing exact-ID `Avalonia` mapping in `NuGet.Config`; peers stay on
  the official feed. Preserve all unrelated packages and source mappings.
- Use fresh build outputs and check the actual Base/Controls DLLs. Do not trust
  an incremental copy from an older Debug instance.

Previous custom package bytes and versioned build inputs are retained unchanged.
Never replace different bytes at an existing package version.

## Consumer validation

- All **46 focused Lumi tests** pass, covering text replacement/reentrant input,
  client selection assignment, direct empty bounds, composer, clipboard, search
  and transcript behavior. The loaded Base/Controls DLL hashes match this package.
- All **642 Strata tests** pass separately on both net8.0 and net11.0.
- All **528 mobile tests** pass.
- Windows desktop Debug/Release, portable net11.0 compilation from Windows,
  Android net10.0 Debug, browser WebAssembly Debug and mobile-desktop Debug builds
  pass. These build checks do not claim device or browser runtime verification.
- An isolated Debug MCP fixture verifies composer selection, shortening and
  clearing text, search filtering/Enter navigation, transcript rendering and
  restored composer focus, with **zero binding errors**.

Keep Strata test outputs inside Strata, such as `Strata\TestResults`, because its
source-location checks walk upward from the test binaries. Run its frameworks
sequentially. The inline-image test keeps window creation, asynchronous loading
and window cleanup in one headless dispatch: each isolated dispatch disposes its
font services. This is a test-lifetime correction, not a production UI change.

## Rebuild

Use PowerShell 7, Python 3, Git/`gh`, and exactly .NET SDK **10.0.201** on PATH,
or install that SDK under the chosen work directory's `dotnet` folder.

```powershell
.\Rebuild.ps1 -WorkRoot 'C:\Temp\Avalonia-12.1.3.1-build'
```

The script uses the pinned official tag and package, declared XamlX submodule,
both accepted source/test patches, upstream unit tests, metadata compatibility
verification, and offline package construction. Results are written under
`verification`; the immutable package is written under `feed`.

## Move back to official Avalonia

Wait for an official **stable** release containing both accepted fixes, not just
their merges or a PR/nightly package. Validate its actual binaries against text
shortening/reentrant input and direct zero-length bounds, then remove only
Avalonia's custom pin, exact local mapping and versioned fork artifacts. Preserve
unrelated vendor content and rerun the app/portable-target checks.
