# Lumi memory leak diagnostics

This folder contains the repo-owned workflow for investigating memory leaks in Lumi's .NET/Avalonia desktop app.

## One-command desktop memory check

On Windows, from the repository root:

```powershell
pwsh tools\memory\Test-LumiMemory.ps1
```

The runner builds optimized Release code with `LumiMemoryCheck=true` into `.mcp-run\memory-check`.
This explicitly includes the existing memory harness; normal Release builds still exclude it.
Every invocation gets fresh isolated data and a unique evidence directory under
`diagnostics\memory\check-...`, and exits its own process when finished.
Profile preparation runs in a separate short-lived process. The measured app only opens the
prepared, checksum-verified data; generating/writing thousands of fixture messages must not
contaminate its initial allocation/GC history. `preparation.log` records that setup cost.

The default workload persists a 2,000-chat synthetic SQLite corpus with varied short, long,
tool-heavy, Markdown/code/document conversations, opens 12 distinct chats, scrolls stored history,
visits all management pages and all actual Settings templates, and returns to the same chat.
Galleries include 40 skills, 8 agents, 96 memories, 24 paused jobs and 4 disabled connectors.
**Fast is the default:** one complete chat/history/page pass, two natural RAM windows and one
post-GC owner/bloat checkpoint. It does not claim sustained-growth or statistical baseline
coverage. The real local Copilot CLI runs; no model inference is sent.
Online catalogs are excluded: changing registry/marketplace data must not change this baseline.
Missing/unrendered content and a missing runtime cannot produce a healthy memory result.

There are two separate passes:

1. **Normal usage:** sample Windows private working set of Lumi and its current child processes
   in windows after opening the chats and after visiting pages. No forced GC/dumps occur here.
2. **Retention/bloat:** force GC after coverage, require released page/surface references
   to die, and check the absolute managed footprint and configured cache bounds.
   Deep mode additionally checks sustained managed/private growth across repeated cycles.
   Render-thread probes require actual drawing and a completed render
   after GC; minimized or unready windows cannot masquerade as healthy low-RAM workloads.

Default targets (MiB = 1,048,576 bytes):

| Gate | Default |
| --- | ---: |
| Average private resident RAM in each measured window, including child processes | 500 MiB |
| Peak private commitment in a measured window | 1,024 MiB |
| Absolute post-GC managed footprint | 200 MiB |
| Expected-dead page/surface references surviving GC | 0 |
| Sustained post-GC managed growth (Deep only) | 24 MiB and 2 MiB/round |
| Sustained private-commitment growth outside the GC heap (Deep only) | 32 MiB and 4 MiB/round |

These are engineering budgets for the declared workload, not a universal guarantee on every PC.
Driver/runtime versions, machine pressure, theme, window size and render scale affect residency.
The report records the actual environment and raw per-process/per-phase samples. Peaks are
reported separately; private commitment, working set and post-GC managed bytes overlap and must
not be added together. Online catalogs, browser pages, voice models and concurrent agent work
are not exercised.

GC heap commitment can oscillate by tens of MiB while live managed memory stays flat. The
non-GC growth gate subtracts the same-process GC commitment before evaluating the trend;
absolute RAM/commitment budgets still include all of it. This is a growth signal, not proof
of a particular native allocator or a leak.

To use your working profile safely:

```powershell
pwsh tools\memory\Test-LumiMemory.ps1 -ProfilePath "$env:APPDATA\Lumi"
```

This takes an online SQLite backup (including committed WAL pages), validates it, and copies
`data.json` into a fresh destination. It never runs against the original profile. Jobs, automatic
AI and remote/OS integration are disabled only in the copy; display settings are normalized.
Copilot sessions, browser data and credential-store files are not copied. Modern SQLite profiles
are required; migrate a legacy profile before using this mode.
The runner explicitly sets `COPILOT_HOME`, and opted-in Release diagnostics also set the SDK's
base directory to that isolated home. The report must verify `RuntimeHomeIsolated` and records
the actual bundled CLI SHA-256; equal version text alone is not equal runtime binaries.

Customize explicit acceptance criteria, for example:

```powershell
pwsh tools\memory\Test-LumiMemory.ps1 -Chats 20 -MaxRamMiB 500
pwsh tools\memory\Test-LumiMemory.ps1 -Mode Deep -Chats 20 -Rounds 4
```

`-NoBuild` reuses only a binary whose source fingerprint matches current code, including
uncommitted/untracked source. `-KeepOpen` keeps the isolated window for inspection and finishes
the command when you close it. Do not send test messages in any existing user chat.

Read the printed `report.txt` for a readable assessment, `summary.json` for the final verdict,
and `results.json` for raw evidence and named retained-view kinds:

- **0 / PASS:** all declared work completed and budgets passed.
- **3 / BUDGET FAIL:** complete workload, but a budget/retention/growth check failed.
- **1 / INCOMPLETE:** startup, sampling, missing coverage, timeout or report error. No valid verdict.

Never silently raise targets or strip content/visuals to make a run green. First compare unchanged
runs on the same host, and prove any new gate fails a known-bad signature or deliberately too-small
budget. Failed runs remain inspectable and are not retried to manufacture a pass.
Private profile copies and raw diagnostics must not be uploaded or committed.

### Fast coverage and measured duration

On the tested machine, a complete fixed synthetic Fast run took **46 seconds** end-to-end;
the frozen 2,192-chat / 1,044,525-message working profile took **96 seconds**. Both opened the
same 12 chats, scrolled stored history, visited all 9 management pages and 10 Settings templates,
checked 9-tail/surface bounds and performed one post-GC lifetime/bloat check. Build time is extra
when source changes.

Fast explicitly waits for the Library's initial scan while its page is active and verifies
visible item controls when it has results. The working-profile run scanned all 2,192 chats and
showed its 6,278-item Library before release; its known retained Library view and RAM/commitment
failures remained red. Merely seeing a loading page is not completed coverage.
Per-stage timings separate chat work, page work and GC verification.

`Test-LumiMemoryRegression.ps1` also defaults to Fast. If its saved baseline exists, Fast uses
that baseline's frozen profile as input, but **does not issue a statistical regression verdict**:

```powershell
pwsh tools\memory\Test-LumiMemoryRegression.ps1
```

Its Fast exits are the ordinary health-check exits: 0 all gates pass, 3 a complete budget/lifetime
failure, 1 incomplete. Fast data cannot be recorded/compared as a Deep baseline. Keep test windows
visible and at their declared size, and keep referenced files/repositories stable.

## Repeatable regression baseline

An absolute budget failure is not the same as a new regression. Record today's footprint
without changing the 500 MiB target:

```powershell
pwsh tools\memory\Test-LumiMemoryRegression.ps1 -Mode Record
```

For the working profile, add `-ProfilePath "$env:APPDATA\Lumi"` when recording. That takes one
safe snapshot; subsequent runs copy its exact `data.json`/SQLite bytes, timestamps and IDs.
`-SnapshotPath <sealed profile directory>` reuses an existing frozen fixture instead.
The default baseline is `diagnostics\memory\baseline.json`; use `-BaselinePath` for a named
baseline or when comparing from another checkout on the same machine. Keep the baseline's
snapshot and archived reference directories; their absolute paths are intentionally local.

Request the explicit thorough comparison with:

```powershell
pwsh tools\memory\Test-LumiMemoryRegression.ps1 -Mode Compare
```

Recording uses **five fresh app processes**, not five rounds in one process, and archives the
exact optimized reference executable and dependencies. Comparison runs **five reference and
five candidate processes**, interleaved with alternating order, against the same frozen input.
This controls today's machine conditions far better than comparing one new RAM value with an
old saved number. The reference archive is hash-checked, and the reference runs must report the
calibrated application/Copilot hashes. Candidate runtime/dependency changes remain part of the
whole-product comparison, not automatic grounds to reset its baseline.

Per-phase resident RAM, overall peak private commitment and post-GC live managed footprint
use a one-sided exact rank/permutation comparison with tied ranks and family-wise correction.
Five observations in each group permit 252 assignments; at most five workload rounds are
supported so a consistent increase can still reach the corrected significance level.
This is not a guarantee of detecting every small change: the report prints medians, ranges,
probabilities and changes, rather than silently inventing a percentage allowance.
On the tested million-message working-profile snapshot, a ten-process comparison took about
42 minutes. This remains explicit Deep/Compare work, not the routine default.
Leave test windows at their declared size
and visible; do not manually minimize/resize them or change referenced repositories/files
during a comparison.
New/increased expected-dead owner counts fail directly. Known baseline lifetime failures stay
visible, as do all absolute budget failures; they are not reclassified as healthy.

The two verdicts and exits are deliberately distinct:

| Regression pipeline result | Exit | Absolute budgets |
| --- | ---: | --- |
| Baseline recorded / no new regression | 0 | Still printed as PASS or FAIL |
| New memory/retention regression | 3 | Printed independently |
| Missing work, changed snapshot, mismatched configuration or bad reference | 1 | No valid regression verdict |

Every report records the input fingerprint and chat order, application/Copilot hashes, runtime
configuration, machine/geometry, per-process RAM/commitment, GC allocation/capacity/collection
counts and physical-memory/CPU pressure. Different profiles, chat order, window/DPI, machines,
runtime configuration or high-memory-pressure classes are incomparable, not regressions.
Physical residency still depends on Windows; GC commitment is capacity, not live objects.
A frozen working profile does not freeze external repositories/attachments it references.
Keep those inputs stable when interpreting its results; the controlled synthetic fixture
does not depend on external user content.

Baselines are never automatically overwritten or ratcheted upward. To accept an intentional
new baseline, record to a new explicit path. Incomplete/failed runs are preserved, never retried
until a pass is obtained. These checks are headed/on-demand, not deterministic CoreQA.

For validation only, `-InjectRetainedMiB 64` in Compare touches and retains an additional
64 MiB inside the candidate process. It is labelled a diagnostic fault, excluded from normal
Release, and forbidden when recording a baseline. It verifies real regression detection,
not merely an impossible absolute-budget threshold.

The November 1 validation recorded five identical-input/binary calibration runs, then a held-out
unchanged comparison (ten complete isolated processes: NO REGRESSION) and the 64 MiB retained
candidate fault (ten complete isolated processes: REGRESSION, exit 3). The fault increased median
post-GC live managed memory by 63.8 MiB and peak private commitment by 59.0 MiB; both metrics and
the first chat-RAM phase crossed the corrected exact-test level. The 500 MiB goal and the known
single Library-view retention remained FAIL in all three stages. This establishes the tested
control behavior, not a promise that every native/GPU leak or small delta is detectable.

## Advisory CI integration

`.github\workflows\memory-qa.yml` is a separate scheduled/manual Windows x64 workflow (plus its
dedicated `validation/memory-tools-fast` branch for hosted verification). It runs the
memory contract/isolation tests and one fully populated **synthetic Fast** pass, then uploads
only reports/logs/TRX, not profile databases. It does not load a developer's private baseline or
copy personal data onto a runner, and it does not add a dependency to PR validation, Extended
UI QA or release publication.

The explicit `-Policy CI` is deliberately a **large-regression guardrail**, not the developer's
500 MiB certificate:

| Finding | Strict local default | CI large-regression policy |
| --- | --- | --- |
| Average private resident RAM | Fail above 500 MiB | Fail above 1,024 MiB; 500 MiB goal remains advisory |
| Peak private commitment | Fail above 1,024 MiB | Fail above 2,048 MiB |
| Post-GC live managed bloat | Fail above 200 MiB | Fail above 200 MiB |
| Retained owners / cache-count violations | Fail | Report advisory; large retained payload still fails memory limits |
| Missing data, content, runtime, rendering or metrics | Incomplete / fail | Incomplete / fail |

```powershell
pwsh tools\memory\Test-LumiMemory.ps1 -Mode Fast -Policy CI
```

CI policy is Fast/synthetic only, and its fixed guardrails are printed in every report. It reports
smaller defects without turning them into unnecessary blockers. A guardrail breach or incomplete
run makes the workflow red, with no `continue-on-error`. These are coarse absolute ceilings, not
a claim to detect every smaller byte delta; use strict local/Deep comparison for diagnosis.
The normal local defaults and their 500 MiB goal remain unchanged. RAM values on `windows-latest`
are not expected to match a developer's PC. A required release RAM gate still needs hosted
repeatability/false-alarm evidence and remains a separate decision.

The same-source local controls prove the reduced workload still detects real bloat:
the fixed synthetic Fast pass completed with all original limits, and its touched/retained
128 MiB fault completed but failed the unchanged 200 MiB managed limit (230.7 MiB measured).
The working snapshot still names its retained Library owner and fails RAM/commitment honestly.

## Automated stress harness

Run the headed DEBUG harness first. It uses an isolated app-data directory, seeds deterministic
heavy chats, repeats real Lumi UI/lifecycle operations, forces full compacting collections between
cycles, and writes a JSON report under `diagnostics\memory`.

```powershell
.\tools\memory\Run-LumiMemoryStress.ps1
```

The default suite covers:

- Unique attachment thumbnails (detects process-lifetime image cache retention).
- Chat surface eviction, including message view-models, transcript turns, and realized hosts.
- Repeated new-chat draft surface replacement.
- Heavy transcript rebuild replacement.
- Detached chat window open/close lifetime.
- Glass frost repaints on the debug transcript fixture, with Skia's GPU cache held over budget as in heavy
  use (detects GPU textures orphaned when the compositor disposes a replaced frost; only private bytes show
  this, the managed heap stays flat).

Run one scenario while investigating:

```powershell
.\tools\memory\Run-LumiMemoryStress.ps1 -Scenarios chat-surfaces -Cycles 10 -ActionsPerCycle 20
```

Available scenario IDs are `attachment-thumbnails`, `chat-surfaces`, `draft-surfaces`,
`transcript-rebuild`, `stable-transcript-scroll`, `detached-chat-window`, and `glass-frost`.
Unknown or unavailable IDs fail the harness instead of silently running a partial filter.

The process exits:

- `0` when every tested scenario passes.
- `3` when expected-dead weak references survive or managed memory keeps growing above both the
  configured total-growth and per-cycle slope gates.
- `1` when the harness itself fails.

The JSON report records managed heap, GC heap/commit/fragmentation, working set, private bytes,
collection counts, threads, handles, tracked weak references, retained reference kinds, and trend
slopes for every measured cycle. Setup, filter, and orchestration failures also emit a JSON failure
report when the configured output location is writable.

Eviction/replacement scenarios enforce enough work to produce expected-dead references even when a
smaller `-ActionsPerCycle` value is requested, and fail rather than reporting a vacuous pass if the
target controls were not realized.

### Persisted chat data and shared history

The synthetic harness keeps autosave off, so its surface checks do not establish that persisted
message models unload. `ChatSurfaceEvictionLeakTests` also exercises disk-backed eviction: a cold,
unhosted chat is saved before its message list is released, and reopening restores the same history.
Save failures, disabled autosave, attached background work and reacquisition during a pending save
must keep the models alive. Eviction saves run one at a time so rapid navigation cannot queue full
clones of every old history at once. In-memory stores never discard their only copy.
Reopening also invalidates an older eviction attempt if the chat is evicted again before that
attempt finishes, so unchanged message counts cannot hide newer edits.

Suggestion history is shared by the data store instead of cached once per chat surface. It keeps
only prompts the existing frequent-request ranker can use; the authoritative chat history remains
unchanged. `SuggestionHistoryTests` checks sharing, refresh, clearing and collection of rejected
large payloads. Clearing chats invalidates both the cache and any in-flight pre-clear result.

Live file-diff snapshots share an identical last-read revision between tool rows, summaries and
later edits. The lookup holds weak references and resets with the transcript; it cannot keep file
text alive by itself. Every capture still reads the file, so external edits (even with unchanged
size/timestamp) and historical revisions stay correct. `TranscriptBuilderToolGroupTests` includes
the focused `FileSnapshots` checks.

### Git status work on evicted chat surfaces

Native Git invocations are serialized process-wide to avoid redirected-pipe handle inheritance
on Windows. A coding-strip refresh queued behind other invocations must still observe its
surface's lifetime. `ChatViewModel` cancels that read-only work on disposal; branch, file/status,
submodule and worktree queries pass the token through to the existing Git runner and its queue.
Version checks still protect newer results, and cached/live chat ownership is unchanged.
Status, diff-stat and submodule queries (including the detached-branch fallback) use
`--no-optional-locks`: otherwise these read-only queries can refresh and write the index,
and killing that optional write on disposal could leave an index lock behind.
The working-tree diff also sets `diff.autoRefreshIndex=false` for that invocation: Git
2.45 can still refresh the stat cache through `diff --numstat` despite `--no-optional-locks`.

`GitServiceTests.PendingSurfaceRefreshStopsBeforeTheGitQueueIsReleased` holds the real queue
closed and verifies that all pending refreshes finish after surface disposal without waiting
for that queue to reopen. `CachedSurfaceCanFinishAndRepeatItsGitRefresh` verifies that an
idle-cached surface still finishes its queued refresh and refreshes again after reacquisition.
The cancellable index-preservation tests in `GitChangesTests` check status line statistics
and detached branch labels without rewriting the index. Run them with `GIT_OPTIONAL_LOCKS=1`
as well: coding tools can inherit `GIT_OPTIONAL_LOCKS=0`, which otherwise masks optional writes.

### Settings UI lifetime

Settings creates only the selected page from a deferred control template. Switching pages
releases the old controls; global search temporarily creates the other pages and releases
them when the query is cleared. Leaving Settings disposes and drops the view rather than
caching it or prewarming it on hover. Values, account/model drafts and cookie-import work
remain owned by `SettingsViewModel`.

Scale teardown commits the ViewModel's preview, not a slider that may not have initialized.
An already-active search is initialized both on first load and when an attached view receives
a replacement ViewModel.

Unbind page data before removing it from the logical/visual tree. Clearing a binding can
change a toggle's state and start a theme animation; doing that after detachment can leave
the animation paused with references to the discarded tree.

`SettingsPageLifetimeTests` checks selected-only construction, global search, retained
ViewModel state and collection of released controls while their owners remain alive.
Use the same working-profile chat/navigation workload before and after a lifetime change;
reachable managed bytes, private commitment and resident RAM are separate measurements.

```powershell
dotnet test tests\Lumi.Tests\Lumi.Tests.csproj --filter "FullyQualifiedName~ChatSurfaceEvictionLeakTests|FullyQualifiedName~SuggestionHistory|FullyQualifiedName~SettingsPageLifetimeTests"
```

## One-time setup

```powershell
.\tools\memory\Initialize-LumiMemoryTools.ps1
```

This restores the local tool manifest, including:

- `dotnet-counters` for live GC/allocation counters.
- `dotnet-gcdump` for lightweight managed heap snapshots and heap-stat reports.
- `dotnet-dump` 10.x for opt-in process dump collection and .NET 11-compatible `gcroot` analysis.

## Start Lumi for a leak investigation

Use the debug agent harness unless the leak only reproduces in the normal app startup path:

```powershell
.\tools\memory\Start-LumiMemoryRun.ps1 -Mode Harness -Scenario transcript-rebuild -CollectCounters -CountersDurationSeconds 300
```

The script builds Debug, starts Lumi, prints the exact PID, and writes run metadata under `diagnostics\memory`.

## Watch live counters on an existing process

```powershell
.\tools\memory\Watch-LumiMemoryCounters.ps1 -ProcessId <PID>
```

Watch for these symptoms while repeating the suspected UI flow:

- GC heap size or working set grows after repeated actions and does not settle.
- Gen 2 collections increase but retained heap remains high.
- Allocation rate remains high while the UI is idle.

## Capture a snapshot

```powershell
.\tools\memory\Collect-LumiMemorySnapshot.ps1 -ProcessId <PID> -Scenario transcript-rebuild -CollectCounters
```

By default this writes:

- `system-runtime-counters.csv` when `-CollectCounters` is used.
- `heap.gcdump`.
- `heapstat.txt`.
- `snapshot-metadata.json`.

Use `-IncludeProcessDump` when you need `dotnet-dump analyze` / `gcroot`. Use `-DumpType Full` only when native memory, handles, or WebView2/browser state are part of the investigation.

## Deep analysis loop

1. Start Lumi with `Start-LumiMemoryRun.ps1`.
2. Capture a baseline snapshot.
3. Use Avalonia MCP to repeat the suspected scenario many times.
4. Capture an after snapshot.
5. Compare `heapstat.txt` files first. Look for Lumi, Avalonia, Strata, WebView2, `ChatMessage`, `ChatViewModel`, control, subscription, timer, and collection types that only grow.
6. Open `.gcdump` files in Visual Studio, PerfView, or JetBrains dotMemory for visual snapshot diffing.
7. If you captured with `-IncludeProcessDump`, use `dotnet-dump analyze` on the `.dmp` and run:

```text
dumpheap -stat
dumpheap -type Lumi.ViewModels.ChatViewModel
gcroot <object-address>
```

The fix is only proven when the retaining root is identified and a repeated after-fix run shows the object count stops growing.

## Transcript UI hangs

For a hang, start with `clrstack -all` and inspect the UI dispatcher's queued callbacks rather than assuming a memory leak.
An attached subagent `RunTimeline` can temporarily have a zero-height `RunTranscriptScroller` viewport.
Viewport realization must wait for size/scroll notifications in that state: continually reposting itself at `Loaded`
priority prevents layout and input from running. The focused regression checks both dispatcher progress at zero height
and successful realization after the viewport grows:

```powershell
dotnet test tests\Lumi.Tests\Lumi.Tests.csproj --filter "FullyQualifiedName~ZeroHeightViewport_DoesNotStarveDispatcher_AndRealizesAfterResize"
```

## Privacy warning

Dump files can include chat contents, prompts, file paths, URLs, environment values, and credentials held in process memory. `diagnostics\memory` is git-ignored; do not commit or share raw dump artifacts.
