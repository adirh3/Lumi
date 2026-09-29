# Vendored Avalonia.Native package

`Avalonia.Native 12.1.3.1-lumi.1` is an **unofficial Lumi-maintained rebuild**,
not an Avalonia release. It changes only the macOS render timer and its internal
managed/native wiring. It is not a replacement for Avalonia 12.1.3's official
native-teardown fix.

## Two separate fixes

- **Official shutdown fix:** Avalonia 12.1.3 includes
  [AvaloniaUI/Avalonia#22200](https://github.com/AvaloniaUI/Avalonia/pull/22200),
  which releases the native macOS platform before .NET runtime teardown. Lumi's
  existing orderly shutdown/save behavior is unchanged.
- **Unofficial screen-off startup fix:** CoreVideo can return -6661
  (`CVDisplayLinkCreateWithActiveCGDisplays`, zero active displays), aborting
  startup before Lumi can serve phone connections. The vendored timer uses
  `DefaultRenderTimer` while a display link cannot be created, configured or
  started, and returns to native vsync after display reconfiguration. It does not
  wake the screen, defer startup, change power settings or add a shutdown/exit
  shortcut.

## Provenance and license

- Package: `Avalonia.Native.12.1.3.1-lumi.1.nupkg`.
- Base: [Avalonia 12.1.3](https://github.com/AvaloniaUI/Avalonia/tree/8eeda4f6f546165b3f72e63c9f42247abb306905),
  commit `8eeda4f6f546165b3f72e63c9f42247abb306905`.
- Proposed upstream fix: **[AvaloniaUI/Avalonia#21453](https://github.com/AvaloniaUI/Avalonia/pull/21453)**,
  open and unmerged when the package was built.
- Reviewed PR head: `d1f740ee9f28a996a21947a511c4d9a0369a3211`.
- Focused upstream commit: `55426bed7e5071dc2ae4029f14179650396f3d85`,
  authored by Brian Pham, co-authored by Cursor.
- Managed targets: `net8.0`, `net10.0`; native dylib: universal arm64 + x86_64.
- Exact dependency: official `Avalonia [12.1.3]`. Other core Avalonia packages
  remain official 12.1.3; DataGrid is separately versioned at 12.1.2.
- MIT license and attribution: `licence.md`, `NOTICE.md`, original source notices
  and the attributed upstream patch are included in the nupkg.
- Package SHA-256:
  `215da26be5653e4d70cafca834fb02a240c1fa356702cf4b079c483d86704e63`.
- Six-file patch SHA-256:
  `f4ce6e2a51d25f8484584825de73533fe23ed5a954815090317693e02b9fc3a6`.

Differences from the proposed fix are limited to reusing `DefaultRenderTimer`,
retaining queued native work with a thread-safe timer-local COM reference count,
queuing native Stop outside callback-thread/managed-lock contexts, handling
unusable starts, and rebuilding the link on completed display reconfiguration.
Existing 12.1.3 window-sizing, positioning and native-teardown changes are retained.

## Consuming and updating

`Directory.Build.props` declares `AvaloniaNativeVersion`; the desktop project
has an exact direct package reference, and `NuGet.Config` maps that **exact
Avalonia package ID**, not the whole family, to this local feed. Ordinary
restore/build/publish therefore select the vendored package without command-line
version overrides. The direct reference is not conditioned on the build host,
so cross-publishing a macOS RID
also gets the paired assets.

**Always ship the managed DLL and dylib together. Their internal ABI differs from
official Avalonia.Native 12.1.3.** The public API, assembly name, version 12.1.3.0
and public-key token are retained. Public signing preserves friend-assembly
identity, not upstream endorsement; the dylib is ad-hoc signed and the nupkg is
not upstream-signed.

This version sorts above 12.1.3 and below 12.1.4. Do not overwrite the bytes of an
already-used version. Any change requires a new package version and updated
provenance. When an official Avalonia release provides the required fallback,
update the core family together and remove the temporary direct Native pin,
local-feed mapping and vendored package. The package's exact core dependency
records the required core ABI; do not bypass dependency-constraint or downgrade
diagnostics to combine it with a newer core.

## Reproducing and verifying the package

The nupkg contains all rebuild inputs under `lumi-source/`: source files, generated
header, upstream key, attributed patches, standalone build projects/script,
focused tests, and `PROVENANCE.json` with dependency/source/binary SHA-256 hashes.
Dependency acquisition/build logic is not added to the Lumi application.

1. Extract the nupkg to a temporary directory.
2. From `lumi-source/build`, run `python3 build.py --fetch-dependencies`.
3. Run `python3 build.py --package`. This builds both managed TFMs and the
   universal dylib, runs the non-GUI regressions/API checks, and packages them.

The recorded environment is .NET SDK 10.0.302, Apple clang 21.0.0 and the macOS 27
CommandLineTools SDK. Full Xcode/CMake are unnecessary. MicroCom's MSBuild/Roslyn
generators are pinned to 0.11.6. Missing pinned dependencies use explicit official
NuGet CDN URLs with normal verified TLS; no global NuGet/TLS settings are changed.
These tools are required only to rebuild the dependency, not to build Lumi.

The original package was rebuilt from its embedded sources with **byte-identical
DLLs and dylib**. Package container bytes can differ when repacking; compare the
binary/source hashes in `PROVENANCE.json`.

### Verification limits

- Two native lifetime/Stop tests passed under AddressSanitizer.
- Ten paired native/managed ABI and timer lifecycle tests passed on arm64 with
  .NET 10.0.10 and .NET 11.0.0-rc.1.26425.128. They exercise no-display fallback,
  failed callback/start, healthy vsync, recovery/loss, callback-thread stop,
  stop/restart ordering and real `ThreadProxyRenderTimer` behavior.
- Both TFMs matched the official assembly identity and 33 public API declarations.
- With the persistent pin, the Lumi app/test graph built and all 25 focused
  `AppShutdownTests`, `UpdateServiceSafetyTests.UpdateShutdown`,
  `SingleInstanceCoordinatorTests` and `CrashReportServiceTests` passed on .NET 11.
- The standalone, trimmed `osx-arm64` Release bundle was inspected without
  execution: its managed Native DLL matches the linker output and its universal
  dylib matches the package byte-for-byte.
- Tests substitute CoreVideo/CoreGraphics calls **at compile time**. They do not
  perform Cocoa swizzling, launch a GUI or change display/power state.
- x86_64 is cross-compiled but not executed. .NET 8 is build/API-checked, not
  runtime-tested.
- The standalone Lumi app bundle passed three real LaunchServices startup/native
  Quit cycles while CoreGraphics reported zero active displays and a separate
  CoreVideo probe returned -6661 before and after every cycle. Quits took
  2.0-2.6 seconds, owned script processes exited, the synthetic chat history was
  byte-identical, and no managed or native crash reports were produced for the
  test processes. No display or power settings were changed.
- A physical display wake after a no-display app launch remains unverified at
  app level; the compile-time substitution tests cover the timer transition.
