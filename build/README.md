# One official Copilot CLI

Lumi uses the same official standalone `copilot` executable for SDK stdio and
interactive login. The managed `Lumi.Copilot.SDK` package is unchanged.

`Lumi.csproj` disables the SDK's default runtime acquisition/copy with
`CopilotSkipCliDownload=true` and imports `Lumi.FullCopilotCli.targets`.
`CopilotCliBinaryPath` must remain unset: that SDK option would re-enable its copy
targets. `Lumi.Tests` also disables the default acquisition and receives Lumi's
verified executable through its project reference.

The only Copilot content item is:

```text
runtimes/{portable-rid}/native/copilot[.exe]
```

It participates in normal build, project-reference copying and publish. Existing
single-file bundling, trimming and platform signing settings are not changed.
SDK callers select this executable with `RuntimeConnection.ForStdio(path: ...)`;
the SDK's default headless-pair discovery and in-process hosting are not used.
Clean output directories are required when checking the layout; old headless
artifacts left by an earlier build are not part of the new content items.

## Acquisition and cache

The SDK's `CopilotCliVersion` selects the version. `CopilotCliPins.props` must have
an exact version/RID match. Windows uses the official standalone ZIP; Linux and
macOS use the official standalone tarball. All eight SDK-supported portable RIDs
are pinned.

The framework-only inline MSBuild task checks the official `SHA256SUMS.txt`
against the checked-in archive hash, verifies the archive before extraction, and
also verifies the extracted executable's checked-in hash. Only the single expected
regular executable is accepted; Unix archives must declare mode `0755`.
Downloads and extraction are staged before a verified executable becomes usable.

Lumi's intermediate directory contains a configuration-independent cache:

```text
copilot-full/{version}/{rid}/
```

With `--artifacts-path`, the cache stays under that artifacts tree. Cache hits
recheck the manifest, archive and executable, work offline, and never modify
NuGet package caches. A checksum failure is an error: remove the reported
version/RID cache directory and rebuild rather than bypassing verification.
`CopilotCliDownloadTimeout` controls the download timeout in seconds (default 600).

## Updating

1. Update the managed SDK intentionally and inspect its selected `CopilotCliVersion`.
2. Obtain that exact official release's `SHA256SUMS.txt` and standalone platform
   archives, not `latest` assets or an unrelated globally installed CLI.
3. Verify each archive against the release checksum, inspect its sole executable
   entry and Unix mode, then compute the executable SHA-256 and update the pins.
4. Run `CopilotPackagingTests`, clean Windows/Linux builds and publishes, and the
   SDK in-memory skill-provider integration using the packaged full CLI. Validate
   native macOS signing/publishing in the existing macOS release environment.
5. Keep independent CLI auto-updates disabled in both SDK and login invocations.

The full CLI self-extracts its embedded package into a versioned Copilot package
cache on first use. One bundled executable does **not** imply a smaller total disk
footprint or unchanged cold-start latency.
