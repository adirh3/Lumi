# Automated QA

The [critical user journey catalogue](critical-user-journeys.md) lists the
release-critical success, failure and recovery paths, their observable
acceptance criteria, and the boundary between current coverage and future work.

Lumi keeps merge validation deterministic. `Category=CoreQA` selects existing
desktop and mobile tests for settings snapshots, session configuration, prompt
platform policy, onboarding, approval rules, SQLite persistence/search,
interactive responses, context curation, mobile framing/trust/pairing, and
loopback client/server flows. The report contract and async test-dispatch
regressions are included too. The curated set also exercises editor state,
edit/regenerate interruption failures, stopping attached work, assistant/session
readiness, background-job dispatch/editing, and management create/save/discard/
delete behaviour. These are component or headless UI tests, not additional
headed user journeys. No account secrets, live model inference, microphone,
external website, timing SLA, or screenshot baseline is required by this gate.

The plugin-resume regression uses the real Copilot runtime with scripted inference
and a per-test runtime home in both Debug and Release. It calls the SDK directly
and checks warm/cold resume, native history, and kept, replaced or cleared plugin
directories without a live account or a Lumi recovery workaround.

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
Use `-ArtifactsPath .mcp-run\qa-local` to build separately from a running Debug
instance; pass the same path with `-NoBuild` when reusing that build.

The component command runs desktop and mobile in separate test hosts. A failing
exit code, missing TRX, zero tests, unexecuted tests, or skipped tests fails QA.
`tools\qa-core-coverage.json` independently registers the selected test methods
for each project. Missing or unregistered methods fail even when other cases
pass, so a lost category marker cannot silently reduce the gate. Parameterized
cases do not need individual entries or brittle count floors. The PowerShell
validator has positive and negative regression tests in `QaCoreCoverageTests`.
`summary.json` includes both results and links naturally to the adjacent TRX and
logs. The script returns nonzero on an incomplete run; it does not retry tests.

## Real headed workflows

The headed suite starts the actual Debug application on a native desktop host.
It uses existing named controls and Avalonia button automation peers, not OS
pointer coordinates or a second implementation of the application:

| Scenario | Independent assertion |
| --- | --- |
| Navigation | Manage projects opens from the drawer; the actual Skills/Projects section buttons change the destination, and Customize remembers it. |
| Project editor | All three inputs use the actual textboxes; the saved project and the index on disk contain the name, instructions, and working directory, and the editor is no longer dirty. |
| AI Features | Every switchable feature and its persisted setting match all-on and all-off; the actual summary is checked at both transitions. Titles start on and finish off, so the live disk check and restart verify a genuinely changed preference. |
| Chat streaming | Input goes through the real composer and a real Copilot runtime. A loopback model holds a partial response until it appears in the actual transcript. |
| Deny | A native file tool cannot write while waiting; clicking Deny leaves no file. |
| Allow | Clicking Allow releases that same native tool path and writes the exact expected contents. |
| Persistence | The application's own autosave stores every completed reply while Lumi is still running. The harness never saves the chat or index after user actions. |
| Process restart | A second process opens the same isolated profile and verifies all automatic-AI flags, project/chat metadata, completed replies, file effects, and the saved transcript in the actual viewport. |

Only the model's OpenAI-compatible streaming responses are scripted. Copilot
still owns sessions, streaming events, tool execution, and native approvals.
Automatic AI features are disabled and only a loopback account/model is
configured. Unknown model requests fail the fixture. The test never sends into
an existing chat and never uses the developer's normal Lumi profile.
The only harness-initiated index save is initial fixture preparation, before the
application UI is constructed. Project/settings save assertions and the live
SQLite checkpoint are read-only observers of production saves.

Text is looked up only in the active chat's transcript. Visibility requires
positive intersection with the window and its clipping ancestors, including
the scroll viewport; a realized buffered turn below the viewport does not pass.
Interactions bring their controls into view before invoking the actual buttons
or updating text inputs. `QaUiVisibilityTests` exercises offscreen, clipped and
transparent controls, and `QaSmokeAssertionTests` rejects partial feature states
and missing, streaming, wrong-role or substring-only persisted replies.

The fixture is Debug-only and its controls do not ship in Release. The pure
options/report types are compiled in both configurations for unit testing, like
the existing UI performance report types.

Headed runs emit a screenshot for each completed scenario, JSON with expected
and executed scenario IDs, separate stdout/stderr, process IDs, architecture,
and the CI commit SHA. Missing, duplicate, failed, unexpected, or unfinished
scenarios fail the report; successful process exit alone is not sufficient.
The second launch uses a separate process ID and cannot seed a replacement
transcript. Timeouts are failures, not skips.

A third launch uses a different fresh profile with `AutoSaveChats=false`. It
must complete the same preceding UI scenarios but fail **only** the live
persistence checkpoint. The driver checks the exact case set, exit status,
failure location and autosave diagnostic; arbitrary failures do not pass this
control. Its nonzero application exit and `complete:false` report are expected,
and the parent summary records `expectedFailure: persist-checkpoint`. If a
future harness saves the transcript itself, that negative launch goes green
and the overall QA command fails. This oracle check runs in Extended UI QA,
not in the merge gate.

## GitHub Actions

`pr-validation.yml` keeps the existing required **Validate** check, solution
builds, Strata validation, Android APK build, PWA publish/JavaScript checks, and
platform-sensitive runtime checks. It runs CoreQA on Windows x64, Linux x64,
macOS Apple Silicon, and macOS Intel. The longer Windows desktop batches and
complete mobile suite run nightly, or via **Run workflow > full_suite**.
This removes account/native/animation-sensitive tests from the ordinary PR
critical path without deleting their broader coverage.

Builds make at most three attempts to download the official Copilot CLI within
the original download deadline. Connection failures, HTTP 408/429 and 5xx
responses are retried with logged backoff; permanent HTTP errors and checksum
failures still fail immediately. Loopback tests exercise the real build task,
its request counts, deadline, cleanup and checked bytes as part of CoreQA.
Tests and headed UI actions themselves are never retried to obtain a pass.
Scroll-position regressions settle the chat-open reveal before capturing their
baseline, rather than relaxing their viewport-preservation assertions.
The browser cookie-reset check waits for the document to observe the completed
native-store deletion; it never repeats the clearing action.
The Strata image-viewer cancellation check waits for asynchronous cleanup to
return its semaphore permits before asserting that none remain in use.

`browser-native-validation.yml` is displayed as **Extended UI QA**. It runs
nightly and on manual dispatch on all four native desktop targets. Each job runs
the headed workflows/restart check, then the existing real native-browser,
docking, screenshot, platform-contract, and Windows WebView2 checks. It is
deliberately separate from the PR **Validate** dependency graph: a graphics
runner problem or a broad QA finding is reported, not hidden with
`continue-on-error`, and does not unnecessarily block an unrelated merge.

Both workflows publish evidence and the QA runner writes an Actions job
summary. UI artifacts explicitly omit the isolated profile directory.

The **Release** workflow calls this same Extended UI QA workflow at the release
commit, in parallel with the PWA and Android builds. All four native targets
must pass before the Windows job can create the draft release; final
publication also depends explicitly on that QA result. Failures, cancellations
and timeouts cannot be bypassed by a successful packaging job. Dry runs include
the same gate but never sign/publish a desktop release. QA receives only a
read-only repository token, not the release-signing secrets. Each release
passes a unique concurrency group, so nightly/manual QA and other release runs
cannot cancel its checks.

To exercise a candidate branch before merging:

```powershell
gh workflow run pr-validation.yml --ref <branch>
gh workflow run browser-native-validation.yml --ref <branch>
gh workflow run release.yml --ref <branch> -f version=1.0.0 -f dry_run=true
```

Schedules run the default branch. An extended workflow result is not a
release-package certification: this suite tests a headed Debug app, not the
installer or a signed/trimmed artifact. Android currently has build, protocol,
and client coverage; there is no claim of Android device/emulator UI coverage.
PWA checks are publish/JavaScript checks, not a real-browser UI suite.
Microphone capture, hardware/OS permission prompts, and real-device or packaged
upgrade testing remain separate coverage gaps. Live model/agent assessments
are not merge gates and are not used as substitutes for coded assertions.

## Browser failure investigation

The investigation branch's manual **Browser failure investigation** workflow is
an experiment, not an added release gate. It records controlled macOS focus/scroll
cases (`LUMI_BROWSER_FOCUS_PROBE=1`) after the unchanged native checks, and holds a
Windows document request until the existing navigation deadline before releasing
that same request. `focus-probe.json` reports observed outcomes, including
non-scrolling cases; a completed experiment or later green smoke run is not proof
that an earlier intermittent failure was fixed.

## Maintaining coverage

Add a focused regression beside its existing component tests. Mark a method or
an entirely portable, deterministic fixture with `[Trait("Category", "CoreQA")]`
only when it is safe without accounts or special host state, and register its
fully qualified method name in `tools\qa-core-coverage.json`. Adding/removing/
renaming a selected method or changing its category requires reviewing that
catalog too; changing theory arguments does not. Do not move a
flaky test into CoreQA or add retries to make a gate green. Keep native/Skia
dispatcher tests in their existing isolated processes.

Add headed scenarios in `QaSmokeHarness`, register their IDs in
`QaSmokeReport.ScenarioIds`, and assert outcomes rather than incidental control
positions, model prose, or elapsed time. Use a named control or an existing
automation peer. Changes to the scenario/report contract have focused tests in
`QaSmokeContractTests`; incomplete evidence must keep failing.
