# Vendored Copilot SDK package

`Lumi.Copilot.SDK` is an **unofficial, temporary prerelease build** of the GitHub
Copilot .NET SDK with the native in-memory skill provider proposed upstream.
It is not an official GitHub release or a statement of upstream API approval.

## Provenance

- Package: `Lumi.Copilot.SDK` version `1.0.14-preview.1.lumi.1`
- Source: [adirh3/copilot-sdk at 4a33b2c](https://github.com/adirh3/copilot-sdk/tree/4a33b2cf5ff7993a58d50aa80a8be008660bf0b7)
- Exact commit: `4a33b2cf5ff7993a58d50aa80a8be008660bf0b7`
- Upstream PR: [github/copilot-sdk#2672](https://github.com/github/copilot-sdk/pull/2672)
- Related issue: [github/copilot-sdk#2651](https://github.com/github/copilot-sdk/issues/2651)
- Native runtime selected by the package: `1.0.84-8`
- SDK targets: `net8.0`, `net10.0`, `netstandard2.0`
- License: MIT; upstream license and attribution are included in the package.
- SHA-256 of `Lumi.Copilot.SDK.1.0.14-preview.1.lumi.1.nupkg`:
  `edfdcec2af7e7561274f5e5bbc3e9bc534cc81e00c78934ec074df0ac3a23a87`

There are no SDK source changes beyond that PR commit. Packaging changes only the
package identity/metadata and names its NuGet `.props`/`.targets` imports after the
new package ID. The assembly remains `GitHub.Copilot.SDK`; do not reference this
package and the official SDK package together.

## Consuming and updating

Normal `dotnet restore`, `dotnet build`, and CI use this local feed automatically.
The SDK selects the compatible CLI version. Lumi disables its default headless
runtime acquisition and packages the matching official full CLI through
[`build/Lumi.FullCopilotCli.targets`](../../build/Lumi.FullCopilotCli.targets),
with archive and executable checksums pinned in `build/CopilotCliPins.props`.
That one executable supplies both SDK stdio and first-party sign-in; the managed
SDK package is unchanged. The `.nupkg` contains the compiled SDK, not a source
checkout or a patch to apply.

Package production belongs outside Lumi's build. A maintainer updating it should
build a new immutable version from an explicit reviewed SDK commit, retain the
correctly named build imports, and update the package reference and provenance here.
Do not replace the bytes of an already-used package version.

The upstream API is experimental and currently text-only/pathless. Once an official
SDK release provides the required API, restore the official package reference and
remove this package and its local-feed mapping.
