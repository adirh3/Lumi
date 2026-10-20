# Lumi memory leak diagnostics

This folder contains the repo-owned workflow for investigating memory leaks in Lumi's .NET/Avalonia desktop app.

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
- `dotnet-dump` for opt-in process dump collection and `gcroot` analysis.

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
