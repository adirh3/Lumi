---
name: Lumi Memory Leak Debugger
description: Diagnose memory leaks in Lumi's .NET/Avalonia desktop app using dotnet-counters, dotnet-gcdump, dotnet-dump, PerfView, Visual Studio, dotMemory, Avalonia MCP, and Lumi-specific retention checks.
---

# Lumi Memory Leak Debugger

Use this skill when the user asks to investigate memory leaks, high memory usage, retained views/viewmodels, slow GC recovery, or Avalonia desktop memory growth in Lumi.

## Core rule

Do not guess. Prove the leak with measurements, prove the owner with a retention path or GC root, then prove the fix with the same scenario.

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

1. Restore tools:
   ```powershell
   .\tools\memory\Initialize-LumiMemoryTools.ps1
   ```
2. Start the focused fixture unless the bug requires normal startup:
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
6. Compare `heapstat.txt` first. Escalate to Visual Studio, PerfView, dotMemory, or `dotnet-dump analyze` when type counts show growth.
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

## Fix patterns

- Prefer ownership methods that release all per-chat resources in one place, like `ReleaseInactiveChatState`.
- Use small disposables (`ActionDisposable`, `DisposableGroup`) for paired event subscriptions.
- Clear dictionaries by chat ID when a chat becomes inactive, is deleted, or loses session ownership.
- Cancel and dispose `CancellationTokenSource` objects only when the owner no longer needs the token.
- Do not add broad catch blocks or silent cleanup failures. Preserve existing Lumi error-handling patterns.
- Add regression tests using `WeakReference`/forced GC for pure viewmodel leaks; use live diagnostics for visual/native leaks.

## Artifact policy

All generated memory artifacts belong under `diagnostics\memory`, which is git-ignored. Dumps can contain private chat text, paths, URLs, tokens, and environment data. Never commit or paste raw dump contents unless they have been intentionally redacted.
