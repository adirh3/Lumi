# Automated QA

Lumi keeps merge validation deterministic. `Category=CoreQA` selects existing
desktop and mobile tests for settings snapshots, session configuration, prompt
platform policy, onboarding, approval rules, SQLite persistence/search,
interactive responses, context curation, mobile framing/trust/pairing, and
loopback client/server flows. The report contract and async test-dispatch
regressions are included too. No account secrets, model inference, microphone,
external website, timing SLA, or screenshot baseline is required by this gate.

## Local commands

Run from the repository root with PowerShell 7.2 or newer:

```powershell
pwsh tools\Test-LumiQa.ps1 -Suite Core
pwsh tools\Test-LumiQa.ps1 -Suite Headed
```

Both commands build what they need. `-NoBuild` reuses an existing build: Release
for Core by default (`-Configuration Debug` selects Debug), always Debug for
Headed. `-ResultsDirectory <path>` changes the output parent; every invocation
creates a unique run directory, so stale reports cannot produce a green result.
On Linux run the headed command inside `xvfb-run -a dbus-run-session -- pwsh`.

The component command runs desktop and mobile in separate test hosts. A failing
exit code, missing TRX, zero tests, unexecuted tests, or skipped tests fails QA.
`summary.json` includes both results and links naturally to the adjacent TRX and
logs. The script returns nonzero on an incomplete run; it does not retry tests.

## Real headed workflows

The headed suite starts the actual Debug application on a native desktop host.
It uses existing named controls and Avalonia button automation peers, not OS
pointer coordinates or a second implementation of the application:

| Scenario | Independent assertion |
| --- | --- |
| Navigation | The selected route and destination controls both change. |
| Project editor | Name, instructions, and working directory reach the saved project; the editor is no longer dirty. |
| AI Features | The actual settings action toggles the features and updates its visible summary. |
| Chat streaming | Input goes through the real composer and a real Copilot runtime. A loopback model holds a partial response until it appears in the actual transcript. |
| Deny | A native file tool cannot write while waiting; clicking Deny leaves no file. |
| Allow | Clicking Allow releases that same native tool path and writes the exact expected contents. |
| Persistence | The database contains the replies before the process exits. |
| Process restart | A second process opens the same isolated profile and verifies settings, project/chat metadata, messages, file effects, and the visible saved transcript. |

Only the model's OpenAI-compatible streaming responses are scripted. Copilot
still owns sessions, streaming events, tool execution, and native approvals.
Automatic AI features are disabled and only a loopback account/model is
configured. Unknown model requests fail the fixture. The test never sends into
an existing chat and never uses the developer's normal Lumi profile.

The fixture is Debug-only and its controls do not ship in Release. The pure
options/report types are compiled in both configurations for unit testing, like
the existing UI performance report types.

Headed runs emit a screenshot for each completed scenario, JSON with expected
and executed scenario IDs, separate stdout/stderr, process IDs, architecture,
and the CI commit SHA. Missing, duplicate, failed, unexpected, or unfinished
scenarios fail the report; successful process exit alone is not sufficient.
The second launch uses a separate process ID and cannot seed a replacement
transcript. Timeouts are failures, not skips.

## GitHub Actions

`pr-validation.yml` keeps the existing required **Validate** check, solution
builds, Strata validation, Android APK build, PWA publish/JavaScript checks, and
platform-sensitive runtime checks. It runs CoreQA on Windows x64, Linux x64,
macOS Apple Silicon, and macOS Intel. The longer Windows desktop batches and
complete mobile suite run nightly, or via **Run workflow > full_suite**.
This removes account/native/animation-sensitive tests from the ordinary PR
critical path without deleting their broader coverage.

`browser-native-validation.yml` is displayed as **Extended UI QA**. It runs
nightly and on manual dispatch on all four native desktop targets. Each job runs
the headed workflows/restart check, then the existing real native-browser,
docking, screenshot, platform-contract, and Windows WebView2 checks. It is
deliberately separate from the PR **Validate** dependency graph: a graphics
runner problem or a broad QA finding is reported, not hidden with
`continue-on-error`, and does not unnecessarily block an unrelated merge.

Both workflows publish evidence and the QA runner writes an Actions job
summary. UI artifacts explicitly omit the isolated profile directory.

To exercise a candidate branch before merging:

```powershell
gh workflow run pr-validation.yml --ref <branch>
gh workflow run browser-native-validation.yml --ref <branch>
```

Schedules run the default branch. An extended workflow result is not a
release-package certification: this suite tests a headed Debug app, not the
installer or a signed/trimmed artifact. Android currently has build, protocol,
and client coverage; there is no claim of Android device/emulator UI coverage.
PWA checks are publish/JavaScript checks, not a real-browser UI suite.
Microphone capture, hardware/OS permission prompts, and real-device or packaged
upgrade testing remain separate coverage gaps. Live model/agent assessments
are not merge gates and are not used as substitutes for coded assertions.

## Maintaining coverage

Add a focused regression beside its existing component tests. Mark a method or
an entirely portable, deterministic fixture with `[Trait("Category", "CoreQA")]`
only when it is safe without accounts or special host state. Do not move a
flaky test into CoreQA or add retries to make a gate green. Keep native/Skia
dispatcher tests in their existing isolated processes.

Add headed scenarios in `QaSmokeHarness`, register their IDs in
`QaSmokeReport.ScenarioIds`, and assert outcomes rather than incidental control
positions, model prose, or elapsed time. Use a named control or an existing
automation peer. Changes to the scenario/report contract have focused tests in
`QaSmokeContractTests`; incomplete evidence must keep failing.
