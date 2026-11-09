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

For the on-demand Windows desktop memory budget and retention workload:

```powershell
pwsh tools\memory\Test-LumiMemory.ps1
```

It defaults to Fast: one fully populated chat/history/page pass and a post-GC owner/bloat
checkpoint. It uses isolated SQLite data, optimized code with explicitly opted-in diagnostics, natural-use
main/child private residency and a separate forced-GC retention pass. The
[memory diagnostics guide](../tools/memory/README.md#one-command-desktop-memory-check) documents
the exact workload, budgets, copied-profile mode, strict completion requirements and limitations.
This headed/host-sensitive budget check is not part of deterministic CoreQA.

For a calibrated same-snapshot regression comparison, record once with
`pwsh tools\memory\Test-LumiMemoryRegression.ps1 -Mode Record`, then run
`pwsh tools\memory\Test-LumiMemoryRegression.ps1 -Mode Compare`. It archives a reference build and compares
interleaved fresh reference/candidate processes; regression and absolute-budget verdicts are
separate. See the [baseline workflow](../tools/memory/README.md#repeatable-regression-baseline).
Without an explicit mode that command also runs the fast health screen, reusing its saved
snapshot when present; it does not issue a statistical baseline verdict.

The standalone `memory-qa.yml` workflow adds scheduled/manual synthetic Fast coverage and
memory-contract tests on Windows. It reports failures directly but is advisory: it is not a
dependency of PR validation or release publication. Initial hosted results must establish
runner-specific behavior before considering a required RAM gate.
Its explicit `-Policy CI` fails only coarse large-memory guardrails (1,024 MiB resident,
2,048 MiB private commitment, 200 MiB post-GC managed). Smaller owner/cache-count/500 MiB goal
findings remain advisory; missing work always fails. Strict local defaults are unchanged.

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

## Mobile smoothness benchmark

`tools\mobile-smoothness\benchmark.py` drives the **same mobile views and deterministic,
paged in-memory PC** in Android and the PWA. It never pairs with a real PC, reads a
saved mobile profile, sends a model request, or changes a user's chats. The fixture
has 1,800 chats, 6,000 logical turns, 72 models across providers, projects, and jobs.
Only the production-sized initial transcript window is generated and loaded:
plain conversation, rich markdown with compact work, and long highlighted code.
The logical code history is tens of megabytes; it is not constructed in memory.

The catalog covers transcript/chat-list/model-list **touch scrolling and flings**,
switching to a different plain/rich/code chat, opening model/project/title sheets,
and moving between Chats, Jobs, Customize, and Settings. Setup, warmups and initial
scroll positioning are excluded. Model opening uses a draft, as the compact
composer exposes its model while composing. Measured input goes through Chromium's
trusted touch injection or Android's native touchscreen input, not command execution
or direct scroll-offset changes.
The host marks the input batch finished only after every injected tap/stroke has
completed. A quiet gap between strokes cannot end recording early; settling and
coverage checks run after that marker. Multi-stroke content cadence includes
gesture boundaries, so read it alongside UI-frame gaps and input/readiness
latency rather than treating every content gap as a UI-thread stall.

Transcript scrolling uses four consecutive real strokes, including their fling,
so it crosses turns that were not built during setup rather than only moving within
one already-built answer. Composer scenarios cover a blank chat and each transcript
kind. Native Android EditText and PWA DOM input events are observed at their host
boundary because those editors do not route their touches through the Avalonia canvas.
`composer-empty` runs first in each fresh app, without a focus warmup, to retain
the first keyboard-opening cost. Other composer cases are warmed as configured.
This is an app-cold tap, not a claim that Android's separate keyboard service or
the device itself was restarted.

`--filter scroll-reveal` specifically measures presentation hitches, not just
motion within an already-built answer. A mixed, bounded page includes questions,
submitted forms, tool rows, terminals, file chips and activity cards. Twelve real
strokes must cross at least three new turns in `scroll-reveal-cold`; the retained
case first visits the page and must return to at least three turns with no cold
builds. Missing coverage fails the action. Each turn's cold/retained activation
and measured layout duration are correlated with frame timestamps in `presentations`.
These diagnostics do not claim that a layout ending is the physical display's paint.

### Build and run

Use optimized, explicitly instrumented builds. Normal Release builds do not include
the fixture or its control endpoint. Debug builds can verify scenarios but are not
an optimized performance baseline.

```powershell
python -m pip install -r tools\mobile-smoothness\requirements.txt

# Publish to a NEW directory; never mix reference and candidate assets.
dotnet publish src\Lumi.Mobile.Browser\Lumi.Mobile.Browser.csproj `
  -c Release -p:LumiMobileSmoothness=true -o C:\bench\reference-pwa

python tools\mobile-smoothness\benchmark.py --platform pwa `
  --web-root C:\bench\reference-pwa\wwwroot `
  --browser-exe "C:\Program Files\Google\Chrome\Application\chrome.exe" `
  --output C:\bench\pwa-reference

# The APK has its OWN com.lumi.mobile.smoothness package and in-memory settings.
# Use android-arm64 for a phone, android-x64 for an x64 emulator.
dotnet publish src\Lumi.Mobile.Android\Lumi.Mobile.Android.csproj `
  -c Release -f net10.0-android -r android-x64 -p:LumiMobileSmoothness=true `
  -p:AndroidSdkDirectory="$env:LOCALAPPDATA\Android\Sdk" `
  -p:JavaSdkDirectory="$env:JAVA_HOME" -o C:\bench\reference-android

python tools\mobile-smoothness\benchmark.py --platform android `
  --adb "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe" `
  --serial emulator-5554 `
  --apk C:\bench\reference-android\com.lumi.mobile.smoothness-Signed.apk `
  --output C:\bench\android-reference

python -m unittest discover -s tools\mobile-smoothness -p "test_*.py"
dotnet test tests\Lumi.Mobile.Tests\Lumi.Mobile.Tests.csproj `
  --filter "FullyQualifiedName~MobileSmoothness"
```

The driver verifies the isolated APK's package with the SDK's `aapt2` **before**
installing it, forwards a loopback-only diagnostic port, and stops only that
benchmark package afterwards. The emulator/device must already be booted and
unlocked. It does not start, reset, reconfigure or kill other emulators.
The simulator also supports `--mobile-smoothness --isolated --device "Pixel 8 Pro"`
for inspecting the shared fixture; its window is not an Android/PWA timing result.

`--filter scroll`, `--filter switch`, `--filter popup`, `--filter nav`, and
`--filter composer` select
catalog prefixes (comma-separated). The default is three fresh app/browser runs
with one unmeasured warmup per action. `--repeats` cannot be less than three.
The PWA uses an actual headed browser surface with a fixed 390 × 844 CSS/DIP
app viewport, not emulated browser DPR. `--width`, `--height`, and an optional
native Chromium `--dpr` override select a form factor. `--workerframes 1` exercises
worker presentation separately from the default normal renderer. `--refresh-hz`
can require a known refresh mode instead of silently accepting a different one.

### What the numbers mean

- **Input to content ready:** the real touch's arrival in the app to the first
  frame opportunity where the intended destination has valid layout/content.
  It includes the normal loading path, not the setup. It is **not hardware
  input-to-photon latency**; scanout, touch-controller delay and pixel response
  require an external camera or platform presentation trace with content correlation.
- **Content frame gaps:** changing transcript offsets sampled at UI frame
  opportunities. A busy UI cannot look smooth merely because an unrelated glow
  keeps drawing. Scrolling also records travel and the viewport's unbuilt fraction
  (p95, maximum and time-weighted exposure); a short blank interval cannot disappear
  simply because it occupies less than five percent of the samples.
- **Composer/keyboard:** real touch to the first UI frame observing editor focus,
  native IME request and the
  OS-reported keyboard inset are separate timings. The latter is not the first
  visible keyboard pixel or a measurement of the keyboard service's animation.
  Composer readiness additionally waits for the inset animation to settle and
  verifies that the laid-out composer is above the keyboard; the first tiny
  inset is not misreported as a fully ready keyboard/composer.
  A desktop-hosted PWA has no soft keyboard: its focus latency is measured, while
  keyboard timings are explicitly unavailable. Android must actually request and
  report an open keyboard or the action fails; hardware-keyboard-only runs do not pass.
- **Rendered output:** Android's actual presentation timestamps for the exact
  Avalonia SurfaceView, collected from SurfaceFlinger; PWA WebGL command batches
  and the headed browser's compositor swaps are separate channels. Neither a
  JavaScript/worker clock nor a GL call count alone is called physical-panel FPS.
  A discrete action with only one redraw has no frame-cadence statistic, not
  a fictitious zero. Continuous scrolling requires multiple rendered frames.
- **Refresh-relative pacing:** gaps, missed-refresh estimates, over-50-ms hitches,
  and excess-gap time accompany readiness, not just average FPS. Raw timestamp
  samples, per-action JSON, native surface dumps/browser traces and before/after
  content screenshots are retained, including failures.

The PWA calibrates its clock on a separate visible animation before loading Lumi,
checks actual canvas/DPR geometry, and rejects hidden/throttled runs. Android
requires one unambiguous app surface and rejects a changed refresh mode or an
incomplete/wrapped frame collector. Both verify that the gesture changed real
content, preserve production paging, and run a **known 120-ms stall**: recorded
frame gaps must span its exact injected interval. Missing input, missing frames,
unreachable controls, incorrect destinations, timeout, malformed output, or
changed build bytes produce incomplete evidence, never a pass.
Android rejects a polling gap long enough for SurfaceFlinger's 128-frame history
to wrap. A disconnected/invalid loopback diagnostic request is logged and closed
without disabling the controller for the next run.

### Comparing changes

Freeze a reference build **with the same harness** before optimizing. Use the same
device, browser binary/version, viewport, scale, renderer, refresh mode, fixture,
warmups and categories, without concurrent builds/tests or another workload.
Repeat reference/candidate runs in alternating order when quantifying a gain;
do not compare desktop-browser rates directly with phone/emulator rates.

```powershell
python tools\mobile-smoothness\benchmark.py `
  --compare C:\bench\pwa-reference\report.json C:\bench\pwa-candidate\report.json `
  --output C:\bench\comparison
```

The report retains repeatability separately from completion: per-scenario
content-gap p95 spread exceeding 20% is noisy, not a certified improvement.
Discrete actions also require repeatable input-to-ready latency; regular frame
opportunities alone cannot certify highly variable chat switching or keyboard opening.
Per-action repeatability is retained alongside the overall result.
Comparisons reject incomplete evidence, mismatched collectors/environments and
unstable runs. **Exit 0** is a complete, repeatable run; **1** is incomplete or
invalid evidence; **3** is a completed but non-repeatable run. Keep failed/noisy
controls rather than selecting the fastest trial or relaxing the threshold.
This host-sensitive development benchmark is not a new required PR/release gate.
Fixture/build configuration comes from the app that actually ran, not a driver
label. Warmup settings, geometry, runtime identity and refresh cadence must agree
across every trial, not only the first. The input-batch marker changes the fixture
contract to v5: freeze new matching references rather than comparing older reports
to the reviewed collector.

Touch moves may be coalesced while the UI is busy. The driver requires a real
scroll gesture and sufficient travel, not an artificially high delivered-move
count that would reject the very stalls being measured. The known stall is
injected after sampled gesture activity begins, and verified against its exact
frame interval.

## Real headed workflows

The Debug responsiveness harness (`dotnet run --project src\Lumi\Lumi.csproj -- --ui-perf-harness --ui-perf-filter navigation`)
includes first-open and repeated Jobs navigation with 24 synthetic jobs (scheduled digests
and paused watchers), rather than measuring an empty overview. Jobs builds a bounded first
page per group and fills toward the viewport while scrolling; its editor is built only when
opened. Component coverage in `ManagementPageLifetimeTests` checks that filtering keeps
this bound, all cards remain reachable, and deferred editors preserve drafts. Multi-group
coverage resumes scheduled jobs after the Paused group has fully loaded, checking that
Enabled fills independently without scrolling. Each gallery measures its own end against
the viewport, so later groups cannot suppress it and offscreen groups still stay bounded.

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
platform-sensitive runtime checks. It runs CoreQA and native CLI/voice packaging
and update-channel checks on Windows x64/ARM64, Linux x64/ARM64, macOS Apple
Silicon, and macOS Intel. ARM64 uses `windows-11-arm` and `ubuntu-24.04-arm`
native runners. The longer Windows desktop batches and
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
The browser's focus-restoration regression runs the actual generated type/fill/
clear setter immediately before scrolling, and requires the offset to remain
after four rendering opportunities rather than accepting one transient sample.
It also checks blur validation, exact field values, type-then-Enter, page-managed
focus and the initial reveal of an offscreen input, including a nested scroller.
Editing first reveals the field with nearest-edge scrolling, then focuses it
without implicit scrolling; restoring focus also suppresses implicit scrolling.
The Strata image-viewer cancellation check waits for asynchronous cleanup to
return its semaphore permits before asserting that none remain in use.

`browser-native-validation.yml` is displayed as **Extended UI QA**. It runs
nightly and on manual dispatch on all six native desktop targets. Each job runs
the headed workflows/restart check, then the existing real native-browser,
docking, screenshot, platform-contract, and Windows WebView2 checks. It is
deliberately separate from the PR **Validate** dependency graph: a graphics
runner problem or a broad QA finding is reported, not hidden with
`continue-on-error`, and does not unnecessarily block an unrelated merge.

Both workflows publish evidence and the QA runner writes an Actions job
summary. UI artifacts explicitly omit the isolated profile directory.

The **Release** workflow calls this same Extended UI QA workflow at the release
commit, in parallel with the PWA and Android builds. All six native targets
must pass before `create-release` creates the draft release; final
publication also depends explicitly on that QA result. Failures, cancellations
and timeouts cannot be bypassed by a successful packaging job. Dry runs include
the same gate but never sign/publish a desktop release. QA receives only a
read-only repository token, not the release-signing secrets. Each release
passes a unique concurrency group, so nightly/manual QA and other release runs
cannot cancel its checks.

Windows packages cross-publish `win-x64` and `win-arm64` on the existing x64
signing host; Linux packages build natively for `linux-x64` and `linux-arm64`.
Each uses an explicit Velopack runtime and a distinct channel. Existing x64
feeds remain `win` and `linux`; ARM64 uses `win-arm64` and `linux-arm64`.
macOS retains `osx-arm64` and `osx-x64`. All desktop uploads merge onto the one
pre-created draft, and publication waits for every architecture. Dry-run
artifact names include the runtime identifier so packages cannot overwrite one
another.

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

## Maintaining coverage

Add a focused regression beside its existing component tests. Mark a method or
an entirely portable, deterministic fixture with `[Trait("Category", "CoreQA")]`
only when it is safe without accounts or special host state, and register its
fully qualified method name in `tools\qa-core-coverage.json`. Adding/removing/
renaming a selected method or changing its category requires reviewing that
catalog too; changing theory arguments does not. Do not move a
flaky test into CoreQA or add retries to make a gate green. Keep native/Skia
dispatcher tests in their existing isolated processes.

Headless pointer tests must flush window layout before locating generated list
containers and measuring click coordinates. Draining dispatcher queues alone
does not guarantee that the next layout pass has realized those containers.
Tests that select a checkout wait for that selection's automatic Git refresh;
starting a duplicate refresh can leave native Git processes using the fixture
directory during cleanup.

Add headed scenarios in `QaSmokeHarness`, register their IDs in
`QaSmokeReport.ScenarioIds`, and assert outcomes rather than incidental control
positions, model prose, or elapsed time. Use a named control or an existing
automation peer. Changes to the scenario/report contract have focused tests in
`QaSmokeContractTests`; incomplete evidence must keep failing.
