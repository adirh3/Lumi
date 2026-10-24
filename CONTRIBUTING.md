# Contributing to Lumi

Focused fixes, documentation improvements, translations and small features are
welcome. Start with an existing [issue](https://github.com/adirh3/Lumi/issues), or
open one describing the problem. Discuss larger features, architectural changes
and new dependencies before implementing them so the scope can be agreed first.
For suspected vulnerabilities, use [private security reporting](SECURITY.md)
instead of a public issue or pull request.

## Tooling

| Required for desktop development | Notes |
|---|---|
| Git | Clone with submodules; Strata is part of the build. |
| [.NET 11 SDK](https://dotnet.microsoft.com/download) | CI currently uses the `11.0.x` preview channel. Check `dotnet --info` if the SDK cannot target `net11.0`. There is no repository `global.json` pin. |
| Internet access for the first build | NuGet restores packages, and the build acquires the checksum-pinned official Copilot CLI. Building does not require an AI account. |

An IDE is optional. Node.js, Python, Android tools and a separately installed
Copilot CLI are **not** required for the ordinary desktop build. Node.js is used
when rebuilding the embedded-app bridge or running JavaScript canvas extensions;
Python 3.10+ is used for the Copilot pin-maintenance script. Android and PWA
development have [additional tooling](docs/development.md#mobile-development).
PowerShell 7 (`pwsh`) is needed for the Windows CI-style batch test command below.

## Fork, clone and branch

Fork [adirh3/Lumi](https://github.com/adirh3/Lumi) on GitHub, then replace
`YOUR-USERNAME` with your GitHub username:

```sh
git clone --recurse-submodules https://github.com/YOUR-USERNAME/Lumi.git
cd Lumi
git remote add upstream https://github.com/adirh3/Lumi.git
git switch -c fix/short-description
```

If Strata is missing after an existing clone:

```sh
git submodule update --init --recursive Strata
```

Keep the checked-out submodule revision unless the change intentionally updates
Strata. In a linked git worktree, never use `git stash`: the stash is shared with
the other worktrees. Do not reset or discard changes you did not make.

## Build the desktop solution

Run from the repository root on Windows, Linux or macOS:

```sh
dotnet build Lumi.slnx --configuration Debug
```

The solution includes the desktop app, mobile shared UI, desktop simulator and
test projects, but not the Android or browser hosts that require extra workloads.
Windows builds target `net11.0-windows10.0.22621.0`; Linux and macOS builds target
`net11.0`. The checked-in NuGet configuration uses NuGet.org; no private feed,
custom Avalonia build or separate SDK checkout is required.

For Linux browser dependencies, macOS browser limits, CLI acquisition and
specialized fixtures, see the [development reference](docs/development.md).

## Run with an isolated profile

**Do not run development builds against your everyday Lumi profile.** Set
`LUMI_APPDATA_DIR` in the same terminal before every development launch.

Windows PowerShell:

```powershell
$env:LUMI_APPDATA_DIR = Join-Path ([System.IO.Path]::GetTempPath()) "Lumi-dev-$PID"
dotnet run --project src\Lumi\Lumi.csproj --configuration Debug
```

macOS or Linux (bash/zsh):

```sh
export LUMI_APPDATA_DIR="$(mktemp -d "${TMPDIR:-/tmp}/lumi-dev.XXXXXX")"
dotnet run --project src/Lumi/Lumi.csproj --configuration Debug
```

Keep that directory to reproduce first-run or restart behavior; reuse its path
instead of making a new one when persistence matters. Close your development
instance before removing its data. An isolated profile keeps Lumi's settings,
chats, skills, sessions and browser data separate and does not change the
launch-at-login registration. Debug plugin installation also uses that profile's
`copilot-home`.

Profile isolation is **not a sandbox**: tools still act with your OS access,
runtime authentication may reuse existing account credentials, and model
requests can consume allowance or API credits. Use disposable files and new test
chats. Do not send test prompts to an existing personal chat or stop another
Lumi instance. For the mobile simulator, also isolate `LUMI_MOBILE_DATA_DIR`
([commands](docs/development.md#mobile-development)).

## Validate the change

Run the smallest existing tests that cover the behavior you changed and add a
focused regression test where appropriate. Documentation-only changes need link,
anchor and template checks, not an application build.

A small **model-free** desktop smoke check is:

```powershell
dotnet test tests\Lumi.Tests\Lumi.Tests.csproj --configuration Debug --filter "FullyQualifiedName~TestProfileIsolationTests|FullyQualifiedName~UiHarnessOptionsTests"
```

On macOS/Linux, the same check uses the Unix project path:

```sh
dotnet test tests/Lumi.Tests/Lumi.Tests.csproj --configuration Debug --filter "FullyQualifiedName~TestProfileIsolationTests|FullyQualifiedName~UiHarnessOptionsTests"
```

These tests do not need an AI sign-in or make model requests; their initial
restore/build still needs the downloads described above. Desktop tests live in
`tests/Lumi.Tests`; mobile tests live in `tests/Lumi.Mobile.Tests`.

For broader Windows validation, use the existing fresh-host batching rather than
combining every graphics/dispatcher fixture into one test process:

```powershell
dotnet build Lumi.slnx --configuration Release
pwsh tools\Test-LumiTestsInBatches.ps1 -Configuration Release -ResultsDirectory TestResults
dotnet test tests\Lumi.Mobile.Tests\Lumi.Mobile.Tests.csproj --configuration Release --no-build
```

The [PR workflow](.github/workflows/pr-validation.yml) runs the deterministic
CoreQA desktop/mobile set on Windows, Linux, Intel macOS and Apple Silicon,
alongside builds and platform-sensitive checks. The broader Windows suites
above run nightly or through the workflow's `full_suite` input, rather than on
every PR. The [QA guide](docs/testing.md) covers the shared local runner, exact
coverage registration and evidence.

[Extended UI QA](.github/workflows/browser-native-validation.yml) runs real
headed workflows nightly/manually and gates Release, without becoming an
unrelated PR merge blocker. The
[critical journey catalogue](docs/critical-user-journeys.md) distinguishes the
required user paths from existing test coverage.

Some tests and harnesses need
a headed desktop, browser runtime, audio hardware, model files, network or an
authenticated account. `CopilotIntegrationTests` are opt-in through
`LUMI_INTEGRATION_TESTS=1`; real model checks may use account allowance. Read the
specific fixture before enabling it, and report skipped/unrun checks honestly.

The test projects isolate their data automatically through `TestProfile`.
**Do not override those variables in tests or build paths to the real profile.**
Use `TestCopilot.Shared` when a test only needs a `CopilotService` reference.
Do not casually construct `new CopilotService()`: it can start a real CLI process.
Tests specifically owning a connection must also own and dispose their service,
as the existing integration fixtures do.

## Follow the existing conventions

- Use C# nullable types, existing formatting and CommunityToolkit.Mvvm patterns
  (`[ObservableProperty]`, `[RelayCommand]`). Keep changes focused.
- Dispatch Copilot callbacks to the Avalonia UI thread. Use Strata controls and
  existing MVVM ownership rather than introducing a parallel UI/runtime layer.
- Put user-facing strings in `src/Lumi/Resources/Strings/en.json` and `he.json`;
  keep keys aligned and use the existing localization helpers, including native
  keyboard-shortcut hints.
- Gate platform-specific APIs and capabilities consistently in tools, prompts
  and UI. Do not advertise Windows desktop automation on Linux/macOS.
- Treat `Chat.Messages` as the live tail, not the whole transcript; use
  `DataStore`'s storage reads for whole-chat behavior.

[AGENTS.md](AGENTS.md) is the deeper engineering reference for ownership,
storage, permissions, UI lifetime, platform rules and diagnostic harnesses.
[Strata development](docs/development.md#local-stratatheme-development) explains
the submodule boundary.

## AI-assisted and automated contributions

AI assistance is welcome, but it does not lower the contribution standard or
transfer responsibility to a tool. A human contributor must review, understand
and validate the complete change, be able to explain it, and ensure they have
the right to contribute the included material under its applicable license.
State which checks you ran and which you could not run.

Bulk or autonomous PR submissions require prior maintainer approval, including
automated cleanup campaigns. Maintainer-approved automation is welcome.
Unsolicited mass submissions, unreviewed generated changes, speculative fixes
and unrelated cosmetic churn may be closed without review. Maintainers are not
obligated to review every submission.

## Open a pull request

Keep the PR to one coherent change. Explain the user-visible problem, your
approach, any linked issue and what you checked (commands, OS/architecture and
results, including checks you could not run). For UI changes, run the real UI,
check bindings and interactions, and attach a redacted screenshot or short
recording; include relevant light/dark or platform differences. Existing
[MCP and UI harness guidance](AGENTS.md#lumi-mcp-and-ui-testing) can help.

Update directly related documentation and localization. Remove credentials,
private prompts, personal paths and unrelated data from screenshots and logs.
Do not upload complete Lumi profiles or chat databases. Follow the
[Code of Conduct](CODE_OF_CONDUCT.md), and keep discussion constructive and
specific; a proposal or PR is not a promise of acceptance.
