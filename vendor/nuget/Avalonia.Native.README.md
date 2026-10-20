# Vendored Avalonia.Native package

`Avalonia.Native 12.1.4.1-lumi.2` is an **unofficial Lumi-maintained rebuild**,
not an Avalonia release. It changes only the macOS render timer and its internal
managed/native wiring. Core Avalonia remains official 12.1.4.

## Scope

- **Official shutdown fix retained:** Avalonia 12.1.4 includes
  [AvaloniaUI/Avalonia#22200](https://github.com/AvaloniaUI/Avalonia/pull/22200),
  which releases the native macOS platform before .NET runtime teardown.
- **Unofficial screen-off startup fix:** CoreVideo can return -6661
  (`CVDisplayLinkCreateWithActiveCGDisplays`, zero active displays), aborting
  startup before Lumi can serve phone connections. Official 12.1.4 still throws
  on this path; it does not supersede the fallback. The vendored timer uses
  `DefaultRenderTimer` while a display link cannot be created, configured or
  started, and returns to native vsync after display reconfiguration. It does not
  wake the screen, defer startup or change power settings.
- **Explicit timer teardown:** the production platform owns terminal timer
  shutdown before factory disposal. Shutdown unregisters the CoreGraphics
  callback, drains native timer work and releases the managed callback roots.
  Stopped/disposed timers cannot notify managed state from queued display work.
  Cleanup no longer depends on a destructor breaking a native/managed retention
  cycle. Ordinary Start/Stop remain nonblocking with respect to the main queue.

## Provenance and license

- Package: `Avalonia.Native.12.1.4.1-lumi.2.nupkg`.
- Base: [Avalonia 12.1.4](https://github.com/AvaloniaUI/Avalonia/tree/2d79ba55fae1b1e71c69f93a668080c9029e6a31),
  commit `2d79ba55fae1b1e71c69f93a668080c9029e6a31`.
- Proposed upstream fix: **[AvaloniaUI/Avalonia#21453](https://github.com/AvaloniaUI/Avalonia/pull/21453)**,
  open and unmerged when the package was built.
- Reviewed PR head: `d1f740ee9f28a996a21947a511c4d9a0369a3211`.
- Focused upstream commit: `55426bed7e5071dc2ae4029f14179650396f3d85`,
  authored by Brian Pham, co-authored by Cursor.
- Managed targets: `net8.0`, `net10.0`; native dylib: universal arm64 + x86_64.
- Exact dependency: official `Avalonia [12.1.4]`. Other core Avalonia packages
  remain official 12.1.4; DataGrid is separately versioned at 12.1.2.
- MIT license and attribution: `licence.md`, `NOTICE.md`, original source notices
  and the attributed upstream patch are included in the nupkg.
- Package SHA-256:
  `fceeffb1e5a09b0dfaf66dcc2afadfb9e0eb8040c343f069e6af6c410c0b1406`.
- Six-file patch SHA-256:
  `07d671133d6c2031cdeb5c8bf2db28e9f6683357f9cc64ffaf6026af7d31999e`.

Differences from the proposed fix are limited to reusing `DefaultRenderTimer`,
retaining queued native work with a thread-safe timer-local COM reference count,
queuing native Stop outside callback-thread/managed-lock contexts, handling
unusable starts, rebuilding the link on completed display reconfiguration, and
explicitly shutting down the timer from platform disposal.
Existing official 12.1.4 behavior outside this timer is retained.

## Consuming and updating

`Directory.Build.props` declares `AvaloniaNativeVersion`; **both desktop
executables**, `Lumi` and `Lumi.Mobile.Desktop`, have an exact direct package
reference. `NuGet.Config` maps that **exact
Avalonia package ID**, not the whole family, to this local feed. Ordinary
restore/build/publish therefore select the vendored package without command-line
version overrides. The direct reference is not conditioned on the build host,
so cross-publishing a macOS RID
also gets the paired assets.

**Always ship the managed DLL and dylib together. Their internal ABI differs from
official Avalonia.Native 12.1.4.** The public API, assembly name, version 12.1.4.0
and public-key token are retained. Public signing preserves friend-assembly
identity, not upstream endorsement; the dylib is ad-hoc signed and the nupkg is
not upstream-signed.

This version sorts above 12.1.4 and below 12.1.5. Do not overwrite the bytes of an
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

Package container bytes can differ when repacking; compare the binary/source
hashes in `PROVENANCE.json`.

### Verification limits

- Five native tests passed under AddressSanitizer, including queued display work
  versus shutdown, Stop suppressing state callbacks, and shutdown waiting for
  an in-flight state callback. The upstream timer fails the queued-retention
  negative control.
- Twelve paired native/managed ABI and timer lifecycle tests passed on arm64 with
  .NET 10.0.10 and .NET 11.0.0-preview.7.26381.103. They exercise no-display fallback,
  failed callback/start, healthy vsync, recovery/loss, callback-thread stop,
  stop/restart ordering, real `ThreadProxyRenderTimer` behavior and the production
  platform's ownership of terminal timer shutdown.
- Both TFMs matched the official assembly identity and 33 public API declarations.
- A full `Lumi.slnx` restore into an empty package cache retained the repository's
  exact source-mapping rules, using local archives of official packages instead
  of the unavailable online feed. Both desktop executable graphs resolved this
  same Native package. This is not a claim of local online-feed validation.
- The full solution built, and all 40 focused
  `AppShutdownTests`, `UpdateServiceSafetyTests.UpdateShutdown`,
  `SingleInstanceCoordinatorTests` and `MainWindowTrayBehaviorTests` passed on .NET 11.
- An isolated headed Debug app loaded byte-matching managed/native package
  assets, completed two normal native macOS Quit requests, and restarted through
  the actual Settings command into the same isolated profile. MCP reported no
  binding errors. Native title-bar close automation was blocked by the machine's
  Accessibility permission; close-to-tray behavior is covered by the focused tests.
- Tests substitute CoreVideo/CoreGraphics calls **at compile time**. They do not
  perform Cocoa swizzling, launch a GUI or change display/power state.
- x86_64 is cross-compiled but not executed. .NET 8 is build/API-checked, not
  runtime-tested.
- A physical no-display startup/wake cycle remains unverified for this rebuilt
  package; the compile-time substitution tests cover the timer transition.
  Results from the older 12.1.3-based package are not evidence for this version.
