---
name: Lumi Memory Leak Debugger
description: Check Lumi desktop memory budgets and investigate leaks or stable bloat with repeatable isolated workloads, retention tests, process/heap measurements and Avalonia diagnostics.
---

# Lumi Memory Leak Debugger

Use this skill when the user asks whether Lumi's memory usage is healthy, to check a RAM budget,
or to investigate leaks, stable bloat, retained views/viewmodels, GC capacity or desktop memory growth.

## Core rule

Do not guess. A high footprint is not necessarily a leak. Prove the workload and measured quantity,
identify ownership before changing code, then prove a fix with the same workload and unchanged UI.

## Start with the repeatable check

From the repository root on Windows:

```powershell
pwsh tools\memory\Test-LumiMemory.ps1
```

This builds optimized Release code with the existing memory harness explicitly opted in. Ordinary
Release builds do not include the harness. The check uses a fresh SQLite profile with 2,000 chats,
opens 12 distinct varied chats, scrolls stored history, visits every management and Settings page,
once by default. Galleries include 40 skills, 8 agents, 96 memories, 24 paused jobs and
4 disabled connectors. It starts the real local Copilot runtime but sends no inference; online
catalogs are excluded so a changing registry/marketplace cannot move the baseline.
Default **Fast** samples natural-use RAM before GC, then checks absolute post-GC bloat,
expected-dead views and configured cache bounds. It waits for the Library's completed scan
and populated item controls while the page is visible; a loading view is not coverage.
Use `-Mode Deep` for repeated rounds and sustained growth. Render-thread draw probes
require actual frames, not just nonzero XAML bounds.
Preparation runs in a separate process: seeding the fixture previously allocated about
153 MiB and triggered Gen 2 before the headed workload. Do not put that setup allocation
history back into the process whose natural-use RAM is being measured.

The defaults are explicit engineering targets, not a universal certificate: 500 MiB average Windows
private resident RAM for Lumi and its child processes in each sample window, 1,024 MiB private
commitment, and 200 MiB post-GC managed memory. The report records actual window size, DPI scaling,
build, architecture, phases, process samples and materialization counts. Do not silently increase
budgets, reduce data, disable visuals or omit a failed action to obtain a pass.

For the user's actual modern profile, request/confirm the copied-profile workload rather than
operating on the live app:

```powershell
pwsh tools\memory\Test-LumiMemory.ps1 -ProfilePath "$env:APPDATA\Lumi"
```

The runner takes an online SQLite backup and copies the index into its own fresh directory. Jobs,
automatic AI, remote access and OS registration are disabled only in the copy. Copilot sessions,
browser profiles and credential-store files are not copied. No test message is sent.
Verify `RuntimeHomeIsolated` in the report. The runner and SDK must both point at the copied
profile's `copilot-home`, including optimized builds; never inherit shared Copilot state.
Use `CliSha256` to control runtime identity in A/B comparisons, not just `--version`.

Use `-Chats`, `-SampleSeconds`, `-MaxRamMiB`, `-MaxPrivateMiB` and `-MaxManagedMiB` only
when the requested acceptance criteria call for them. `-NoBuild` rejects stale source fingerprints.
`-Rounds` belongs to Deep mode. The local fixed synthetic Fast control took 46 seconds;
the full million-message working snapshot took 96 seconds, with all 12 chats/pages/history
and its 6,278-item Library covered. Build time is additional.
`-KeepOpen` leaves only that isolated instance available for debugging; the command finishes when
the window closes. Never attach test sends to an existing user chat.

Read `summary.json` and `results.json` in the printed unique run directory:

- **Exit 0 / PASS:** complete declared workload and all budgets passed.
- **Exit 3 / BUDGET FAIL:** workload complete, but RAM/bloat/retention/growth exceeded a limit.
- **Exit 1 / INCOMPLETE:** no valid memory verdict. Diagnose missing content, startup, sampling,
  coverage, report or timeout errors first. An empty or partially executed run is never healthy.

`report.txt` is the readable assessment. Retained references are named by their page/owner kind;
use those names to select a targeted investigation, not a speculative full-app rewrite.

Do not treat this as coverage of browser pages, dictation, concurrent agents or packaged installs.
It is a headed reading/navigation workload. Use the existing focused stress scenarios or an
explicit additional workload when those omitted features are the user's concern.

## Detect regressions against a controlled baseline

```powershell
pwsh tools\memory\Test-LumiMemoryRegression.ps1 -Mode Record
pwsh tools\memory\Test-LumiMemoryRegression.ps1 -Mode Compare
```

Add `-ProfilePath "$env:APPDATA\Lumi"` when recording to freeze the working profile, or
`-SnapshotPath <sealed directory>` for an existing fixed fixture. `-BaselinePath` names a
baseline; existing baselines are never overwritten automatically.
Without an explicit mode, this command also runs Fast, using the saved snapshot when present.
Fast is a health screen (0 all gates pass, 3 budget/lifetime failure, 1 incomplete), not a
statistical baseline verdict. It cannot be used as a Deep calibration run.

Record uses five independent app processes and archives their reference build. Compare uses
five fresh reference and five candidate processes with alternating/interleaved order, all on
the exact snapshot bytes and chat order. An old RAM number alone does not control today's
environment. Verify application/Copilot hashes, runtime settings, geometry and machine.
GC allocation/capacity/collections and machine-pressure counters accompany the natural samples.

Regression status is separate from the absolute 500 MiB target. Exit 0 means recorded/no new
regression, not that the budget or existing retained-owner failures are healthy. New/increased
owner counts fail; baseline failures remain visible. Per-phase RAM and private/managed summaries
use exact one-sided ranked comparisons with family-wise correction, printing ranges and
probabilities, not an invented percentage tolerance. Only three to five workload rounds are
supported for the five-run test's statistical resolution. Missing/changed input, reference,
runtime configuration or coverage is INCOMPLETE, never a pass.

Validate with an unchanged interleaved control and a Compare-only `-InjectRetainedMiB 64` fault.
Retain all results; do not rerun until green. A frozen profile does not freeze referenced
repositories/files. Keep those stable for real-profile comparisons; do not remove content or
visuals to get a lower baseline. Details and exact commands are in `tools\memory\README.md`.

The standalone `memory-qa.yml` adds scheduled/manual synthetic Fast coverage and memory
contract tests on Windows. It is advisory, not a PR/release dependency. Failures stay red;
its explicit `-Policy CI` uses large-regression guardrails: 1,024 MiB RAM, 2,048 MiB private
commitment, and 200 MiB post-GC managed. Smaller retained-owner/cache-count and 500 MiB goal
findings are advisory, not unnecessary blockers; missing coverage always fails. This policy is
Fast/synthetic only and does not change strict local defaults. There is no private-profile upload.
Hosted validation uses the dedicated `validation/memory-tools-fast` branch. Hosted runs still need
runner-specific validation before considering a required release RAM gate.

## Interpret the evidence correctly

- **Private working set:** resident private physical pages; Windows can trim or page them under
  pressure. A budget comparison needs comparable machine load, geometry, scale and build.
- **Private commitment:** allocated private process memory, including managed/native capacity.
  It is not the same as RAM, and is not exclusively native/GPU allocation.
- **Post-GC managed bytes:** retained managed footprint in the diagnostic pass, not natural usage
  and not a CLRMD strong-root/dominator attribution.
- **GC commitment versus private growth:** GC capacity can expand or oscillate without new live
  objects. The non-GC growth signal uses same-process private commitment minus GC commitment;
  absolute budgets still include the capacity. A rising private endpoint alone is not a leak.
- **Leak:** expected-dead owners remain reachable, or repeated warmed-up work keeps growing.
  Find a retaining path before declaring a cause.
- **Bloat:** excessive live or cached objects at a stable plateau. A flat growth graph alone does
  not clear it; check the absolute budget and ownership.

Never add overlapping metrics, count shared working-set pages several times, trim a working set
to manufacture a pass, or compare post-dump residency with untouched normal-use samples. Dumps
touch pages and diagnostic tools allocate memory. Sample natural usage first.

Calibrate comparisons on repeated unchanged runs. Require a known-bad signature or a deliberately
too-small threshold to fail before trusting a new gate. Missing phases/metrics, unrendered content
and omitted child runtime memory must fail rather than default to zero.

## Tools to prefer

| Tool | Use it for | Repo command |
| --- | --- | --- |
| `dotnet-counters` | Live symptoms: GC heap, allocation rate, Gen 2 pressure, thread count, exceptions | `.\tools\memory\Watch-LumiMemoryCounters.ps1 -ProcessId <PID>` |
| `dotnet-gcdump` | Lightweight managed heap snapshots and type-count heap stats | `.\tools\memory\Collect-LumiMemorySnapshot.ps1 -ProcessId <PID>` |
| `dotnet-dump` | Deep dump analysis, `dumpheap`, object addresses, `gcroot` retention paths | `.\tools\memory\Collect-LumiMemorySnapshot.ps1 -ProcessId <PID> -IncludeProcessDump` |
| PerfView | Baseline/after snapshot comparison, allocation stacks, reference graph | Open the generated `.gcdump` or collect PerfView traces manually |
| Visual Studio Diagnostic Tools | Visual memory snapshots and retention graph on Windows | Open `.gcdump` or `.dmp` |
| JetBrains dotMemory | Fast before/after comparisons and visual dominator/root paths | Attach to Lumi or open dumps |
| Avalonia MCP | Reproduce UI scenarios deterministically and inspect detached controls/bindings | Use the debug harness and stable `#PageChat`, `#Transcript`, `#Composer` landmarks |

Primary references:

- Microsoft .NET diagnostics memory leak tutorial: <https://learn.microsoft.com/dotnet/core/diagnostics/memory-leak-tutorial>
- `dotnet-counters`: <https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-counters>
- `dotnet-gcdump`: <https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-gcdump>
- `dotnet-dump`: <https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-dump>
- PerfView user guide: <https://github.com/microsoft/perfview/blob/main/src/PerfView/SupportFiles/UsersGuide.htm>
- JetBrains dotMemory: <https://www.jetbrains.com/dotmemory/>

## Lumi investigation workflow

1. Run the repeatable check first. For a focused lifetime issue, use the existing stress harness:
   ```powershell
   pwsh tools\memory\Run-LumiMemoryStress.ps1 -Scenarios chat-surfaces
   ```
2. If the result needs attribution, start an isolated Debug fixture:
   ```powershell
   .\tools\memory\Start-LumiMemoryRun.ps1 -Mode Harness -Scenario <short-name> -CollectCounters
   ```
3. Capture a baseline:
   ```powershell
   .\tools\memory\Collect-LumiMemorySnapshot.ps1 -ProcessId <PID> -Scenario <short-name>-baseline
   ```
4. Reproduce with Avalonia MCP. Repeat the suspected action enough times to make retention visible. For chat leaks, use the debug harness fixture and stable controls: `#PageChat`, `#ChatShell`, `#Transcript`, `#Composer`.
5. Capture an after snapshot:
   ```powershell
   .\tools\memory\Collect-LumiMemorySnapshot.ps1 -ProcessId <PID> -Scenario <short-name>-after
   ```
6. Compare `heapstat.txt` first. Escalate to Visual Studio, PerfView, dotMemory, or a runtime-compatible
   `dotnet-dump analyze` when counts/bytes identify a suspect. Lumi's .NET 11 dumps require the
   pinned 10.x dump analyzer; the old 9.x analyzer cannot reliably analyze this runtime. Restore
   tools only if missing or stale.
7. In `dotnet-dump analyze`, use:
   ```text
   dumpheap -stat
   dumpheap -type <Suspect.Type.Name>
   gcroot <object-address>
   ```
8. Fix the owning reference. Add or update a regression test when the leak is in viewmodel/runtime ownership; use live diagnostics when it depends on Avalonia visual lifetime.
9. Re-run the same scenario and compare after-fix snapshots.

## Lumi-specific leak checklist

Check these first because they are common in this codebase and Avalonia desktop apps:

- `ChatViewModel` runtime dictionaries: `_runtimeStates`, `_ctsSources`, `_sessionSubs`, `_sessionCache`, `_inProgressMessages`, `_pendingQuestions`, `_chatBrowserServices`.
- Event subscriptions: every `+=` from a long-lived owner needs a matching `-=` or a disposable wrapper. Pay special attention to `PropertyChanged`, `CollectionChanged`, Copilot service events, `ShutdownRequested`, and visual-tree events.
- Dispatcher work: delayed `Dispatcher.UIThread.Post`, timers, throttlers, and cancellation tokens can keep closures alive.
- `CancellationTokenSource` and `TaskCompletionSource`: cancel and remove them when chats switch, sessions die, or questions expire.
- Browser/WebView2 services: dispose per-chat browser services and native resources when a chat no longer owns live browser work.
- Avalonia control graphs: detached views can stay alive through event handlers, bindings, data contexts, static resources, animations, or template parts.
- Transcript rebuilds: repeated creation of `StrataChatMessage`, markdown, reasoning, tool call, attachment, and generated-file controls should not grow retained visual/control counts after the transcript is replaced.
- Static caches and services: verify caches have clear ownership and bounded growth.
- Broad class selectors: unrelated controls can retain inactive style frames, class activators,
  setter state and resource expressions. Scope component helpers to static owners while retaining
  class/pseudo-class conditions and priority. Trace all consumers: the collapsed rail, popup roots,
  host slots and launchpad setups may have different ancestry; diff colors are shared utilities.
- Discarded management and Settings pages: keep their view models/drafts but release their UI.
  Logical and visual descendants and paused template animations can retain a detached tree.
- SQLite history: distinguish stored messages, live tails, built older exchanges and intentionally
  cached surfaces. Opening N chats does not imply keeping N whole transcripts in memory.

## Fix patterns

- Use the existing ownership boundary; do not clear active work just because the assistant is idle.
- Use small disposables (`ActionDisposable`, `DisposableGroup`) for paired event subscriptions.
- A chat switch is not proof its session is dead. Preserve attached processes, agents, pending sends,
  drafts and resumable history; release only owners the repository's lifetime policy says are dead.
- Cancel and dispose `CancellationTokenSource` objects only when the owner no longer needs the token.
- Do not add broad catch blocks or silent cleanup failures. Preserve existing Lumi error-handling patterns.
- Add regression tests using `WeakReference`/forced GC for pure viewmodel leaks; use live diagnostics for visual/native leaks.

## Artifact policy

Generated runs belong under `diagnostics\memory`, which is git-ignored; builds live separately in
`.mcp-run\memory-check`. Copied profiles, reports and dumps can contain private paths/content.
Keep them local and never upload or commit raw profiles/dumps. The runner closes its own process
by default; when debugging, record PID/executable/start time and stop only that owned instance.
