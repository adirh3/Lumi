# Lumi memory leak diagnostics

This folder contains the repo-owned workflow for investigating memory leaks in Lumi's .NET/Avalonia desktop app.

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

## Privacy warning

Dump files can include chat contents, prompts, file paths, URLs, environment values, and credentials held in process memory. `diagnostics\memory` is git-ignored; do not commit or share raw dump artifacts.
