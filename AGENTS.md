# Lumi — Agent Guidelines

## Project Summary

Lumi is a cross-platform Avalonia desktop app — a personal agentic assistant that can do anything. It is a chat application with a modern, intuitive UX that feels alive. Lumi's main interface is a chat interface powered by GitHub Copilot SDK as the agentic backend. Single-project solution with MVVM architecture using CommunityToolkit.Mvvm source generators.

## Tech Stack

- **.NET 11** with C# and nullable reference types
- **Avalonia UI 12.0.4** — cross-platform desktop framework
- **CommunityToolkit.Mvvm 8.4** — MVVM source generators (`[ObservableProperty]`, `[RelayCommand]`)
- **GitHub.Copilot.SDK** — agentic backend for LLM interaction
- **StrataTheme** — custom UI component library (external project reference at `../../../Strata/src/StrataTheme/`)

## Core Concepts

- **Chat** — A chat session with Lumi. Primary interaction surface. Has message history and can be linked to a project and/or agent.
- **Project** — Named collection of chats with custom instructions injected into the system prompt.
- **Skill** — Reusable capability definition in markdown (e.g., "Word creator" converts markdown to Word via Python). Listed in system prompt so the LLM knows what's available.
- **Lumis (Agents)** — Custom agent personas combining system prompt, skills, and tools (e.g., "Daily Lumi" checks mail/todos and plans the day). Users create and select them.
- **Memory** — Persistent facts extracted from conversations, included in system prompt across all sessions.

## User Flows

- **Onboarding** — First launch asks the user's name, then greets them personally.
- **Chat** — Streaming chat with tool call visualization, reasoning display, typing indicators.
- **Memories** — Important info is remembered across sessions via system prompt injection.
- **Skills** — Users create skills and reference them from any chat.
- **Agents** — Users create agents and select them in the agents tab.
- **Projects** — Chats are organized into projects with custom instructions.
- **Context awareness** — `SystemPromptBuilder` assembles: active project, agent, time of day, user name, skills, and memories.

## UX Principles

- Modern and alive — animated components, responsive interactions, StrataTheme design system.
- Not bloated — main interface focuses on chats with clean navigation.
- Welcome experience — elegant welcome panel with suggestion chips.
- Transparency — tool calls grouped with friendly names, reasoning tokens displayed, streaming indicators.
- Dedicated management — agents, skills, projects each have master-detail CRUD with search.

## Architecture

```
App.axaml.cs
  ├── DataStore          (JSON persistence → %AppData%/Lumi/data.json)
  ├── CopilotService     (GitHub Copilot SDK wrapper)
  └── MainViewModel
        ├── ChatViewModel      → DataStore, CopilotService, SystemPromptBuilder
        ├── SkillsViewModel    → DataStore
        ├── AgentsViewModel    → DataStore
        ├── ProjectsViewModel  → DataStore
        └── SettingsViewModel  → DataStore
```

- **Models** (`src/Lumi/Models/Models.cs`): All domain entities in one file — `Chat`, `ChatMessage`, `Project`, `Skill`, `LumiAgent`, `Memory`, `UserSettings`, `AppData`
- **Services** (`src/Lumi/Services/`): `CopilotService` (SDK wrapper with streaming events), `DataStore` (JSON persistence to `%AppData%/Lumi/data.json`), `SystemPromptBuilder` (composite system prompt)
- **ViewModels** (`src/Lumi/ViewModels/`): `MainViewModel` (root), `ChatViewModel` (streaming chat), `AgentsViewModel`, `ProjectsViewModel`, `SkillsViewModel`, `SettingsViewModel` — all CRUD follows same pattern
- **Views** (`src/Lumi/Views/`): Avalonia XAML + code-behind. `ChatView.axaml.cs` is the heaviest — builds transcript programmatically using Strata controls
- **External dependency**: StrataTheme UI library referenced as a git submodule at `Strata/` — provides `StrataChatShell`, `StrataChatMessage`, `StrataMarkdown`, `StrataThink`, `StrataAiToolCall`, etc. If a build fails because Strata files are missing, run `git submodule update --init --recursive Strata` from the repo root and retry.

### Key Patterns

- **MVVM** with CommunityToolkit.Mvvm source generators — use `[ObservableProperty]` for bindable properties and `[RelayCommand]` for commands
- **Event-driven streaming** — `CopilotService` events → `Dispatcher.UIThread.Post` → ViewModel state → View reactivity
- **Programmatic UI construction** — `ChatView.axaml.cs` builds the chat transcript dynamically using Strata controls (not data templates)
- **JSON file persistence** — single `data.json` file via `DataStore`, no database
- **System prompt composition** — `SystemPromptBuilder` assembles context from user name, time of day, agent, project, skills, and memories

### Strata UI Controls Used

- `StrataChatShell`, `StrataChatComposer`, `StrataChatMessage` — chat layout
- `StrataMarkdown` — markdown rendering
- `StrataThink`, `StrataAiToolCall` — tool call display
- `StrataTypingIndicator` — streaming indicator
- `StrataAttachmentList`, `StrataFileAttachment` — file attachments

## Project Structure

```
src/Lumi/
  ├── Models/Models.cs         — All domain entities (Chat, Project, Skill, LumiAgent, Memory, etc.)
  ├── Services/
  │   ├── CopilotService.cs    — GitHub Copilot SDK integration
  │   ├── DataStore.cs         — JSON persistence
  │   └── SystemPromptBuilder.cs — Dynamic system prompt assembly
  ├── ViewModels/              — MVVM ViewModels
  │   ├── MainViewModel.cs     — Root VM, navigation, chat list management
  │   ├── ChatViewModel.cs     — Active chat state, message streaming
  │   ├── AgentsViewModel.cs   — Agent CRUD
  │   ├── ProjectsViewModel.cs — Project CRUD
  │   ├── SkillsViewModel.cs   — Skill CRUD
  │   └── SettingsViewModel.cs — User settings
  └── Views/                   — Avalonia XAML views + code-behind
      ├── MainWindow.axaml(.cs) — App shell, sidebar, navigation
      ├── ChatView.axaml(.cs)   — Chat transcript (heavy code-behind)
      ├── AgentsView.axaml(.cs) — Agent management
      ├── ProjectsView.axaml(.cs) — Project management
      ├── SkillsView.axaml(.cs)  — Skill management
      └── SettingsView.axaml(.cs) — Settings page
```

## Domain Model

| Entity | Purpose |
|--------|---------|
| `ChatMessage` | Single message with role (user/assistant/system/tool/reasoning) |
| `Chat` | Conversation with message history, linked to project/agent |
| `Project` | Collection of chats with custom instructions |
| `Skill` | Reusable capability definition (markdown content) |
| `LumiAgent` | Custom agent persona with system prompt, skills, tools |
| `Memory` | Persistent user fact extracted from conversations |
| `UserSettings` | App preferences (name, theme, model) |
| `AppData` | Root container for all persisted data |

## Code Style

- C# with nullable reference types enabled, implicit usings
- `[ObservableProperty]` on fields (e.g., `string _name;` → generates `Name` property)
- `[RelayCommand]` on methods for bindable commands
- `partial void On<PropertyName>Changed()` for property change side effects
- All Copilot event handlers must dispatch to UI thread via `Dispatcher.UIThread.Post()`

## Focused Engineering

Lumi prefers the smallest complete solution: everything required for the requested behavior to work correctly on supported, realistic paths, without speculative infrastructure or hardening.

### Scope and implementation

- Before editing, identify the requested observable behavior, how it will be verified, and what behavior must remain unchanged.
- Make the smallest coherent change across existing ownership boundaries. Necessary model, service, ViewModel, UI, persistence, and test wiring is in scope; unrelated cleanup or redesign is not.
- Prefer direct code and existing patterns or helpers. Introduce a new abstraction, service, state machine, protocol, compatibility layer, or configuration surface only when it is the most straightforward and cohesive way to satisfy a concrete current requirement, not for hypothetical reuse or future needs.
- Handle the normal-use inputs and failures applicable to the changed path, including empty, missing, first-run, cancellation, and error states, using established repository patterns. Do not add speculative global hardening such as new persistence frameworks, generalized concurrency or merge/version protocols, speculative migrations, retry/rollback frameworks, generalized validation, or unrelated UI/platform infrastructure.
- Do not fix pre-existing issues unless they directly block the requested behavior. Report important unrelated issues separately.
- Before creating a new subsystem, changing behavior outside the requested path, or materially expanding the production implementation, identify the concrete requirement or demonstrated regression that requires it. If the connection is indirect or debatable, keep the smaller in-scope solution and surface the larger change as a follow-up. Ask for approval before expanding; if approval is unavailable, defer it.

### Handling review feedback

- Treat every review finding, whether human, automated, or from a subagent, as a hypothesis. Verify it against the current diff, requested behavior, and a supported realistic scenario before changing code.
- Fix a finding within the current change only when all of the following are true:
  1. It was introduced by the current change, or fixing it is necessary for the requested behavior to work correctly on the changed supported path.
  2. It is high-confidence and reachable in a realistic supported scenario.
  3. Its impact is material, such as incorrect behavior, a crash, data loss, a security issue, a meaningful user-visible regression, or a clear violation of an established repository boundary introduced by the change.
  4. It has a focused fix that does not create a new subsystem or intentionally change behavior outside the requested path.
- Classify findings that fail this gate as separate follow-ups and state why they are outside the current change. Report them, but do not implement them without explicit approval.
- New build failures or existing test failures caused by the current change are always in scope.
- A review-only request, including "review" or "run code review," is read-only. A request to "address the review" authorizes only verified, in-scope findings, not every suggestion.
- Never broaden the implementation merely to obtain an `APPROVE` verdict or eliminate all reviewer comments.
- After accepted fixes, rerun relevant validation. Repeat review only when the fixes materially changed the logic or new evidence warrants it; do not enter recursive review-and-hardening loops.
- Completion is determined by the requested behavior and validation results, not by reviewer silence.

### Proportional validation

- Run the existing builds, tests, and harnesses relevant to the changed surface.
- Add focused tests in the existing relevant test project or harness for the requested behavior, likely regressions, and important boundaries on the changed path.
- If no suitable automated test surface exists, use the existing relevant validation path, such as MCP or focused manual verification, rather than creating generalized test infrastructure solely for the task.
- Do not create combinatorial test matrices, generalized test infrastructure, new harnesses, or platform-wide stress coverage solely for theoretical edge cases.
- Expand validation only when the changed surface, acceptance criteria, or an observed failure justifies it.
- The task is complete when the requested behavior is verified, relevant validation passes, and no known in-scope blocker remains.

## Build & Test

```bash
dotnet build src/Lumi/Lumi.csproj
cd src/Lumi && dotnet run
```

Automated tests live in `tests/Lumi.Tests`. StrataTheme is referenced via the `Strata/` git submodule.

### Lumi MCP and UI Testing

Lumi has two repo-configured MCP servers in `.vscode/mcp.json`:

- `lumi-mcp` — Lumi-specific Debug/E2E control surface. Prefer this first for app workflows: launch isolated Debug Lumi, open/create chats, send messages, wait for idle, read transcript/activity, navigate pages, list/configure features, and run harnesses.
- `avalonia-mcp` — generic Avalonia UI diagnostics. Use this for visual/layout checks, control trees, bindings, focus, styles, screenshots, and interaction validation.

For efficient agent validation, use `lumi-mcp` for stateful Lumi actions and structured assertions, then cross-check visible UI and binding health with `avalonia-mcp`.

### UI Testing with Avalonia MCP

Lumi has an Avalonia MCP server configured in `.vscode/mcp.json`. This gives you live access to the running app — you can see the UI, click buttons, type text, inspect controls, check bindings, and take screenshots. **Use it.**

**Every time you make a UI change, you must test it with the MCP tools.** Don't just build and hope it works — run the app, poke at it, and confirm your changes look and behave correctly. This is your primary way of verifying UI work since there are no UI tests.

#### Workflow

1. Run `dotnet tool restore` once to ensure the CLI tool is available
2. Start Lumi: `cd src/Lumi && dotnet run`
3. Use the MCP tools to verify your work

#### Debug-only agent harnesses

Lumi includes focused Debug-only harnesses for coding agents:

- `dotnet run --project src\Lumi\Lumi.csproj -- --debug-agent-harness` opens Lumi directly into a synthetic transcript fixture chat. The fixture is not saved to normal chat history and exercises user/assistant messages, reasoning, tools, subagents, questions, errors, sources, attachments, plan content, token metadata, and generated files.
- `dotnet run --project src\Lumi\Lumi.csproj -- --test-chat-stress` runs a headless real Copilot stress check. It requires a deterministic tool invocation and exits nonzero if the expected `LUMI_CHAT_STRESS_OK` response is not produced.
- `dotnet run --project src\Lumi\Lumi.csproj -- --ui-perf-harness` runs the **UI responsiveness harness**: it measures how laggy the UI feels *from the user's perspective* under a full stress load, then prints a console report and writes JSON ranking the worst UX actions/categories. Details below.
- When running a Debug build, the top-right `#AgentDebugMap` overlay shows stable navigation indices, current page landmarks, chat control IDs, and an `Open fixture` button.
- Stable MCP/UI automation landmarks are available by name: `#NavChat`, `#NavProjects`, `#NavSkills`, `#NavAgents`, `#NavMemories`, `#NavMcpServers`, `#NavSettings`, `#PageChat`, `#ChatShell`, `#Transcript`, and `#Composer`.

Use these harnesses before making transcript or chat UI changes, and keep the specific debug PID running when the user should inspect the visible result.

#### UI responsiveness harness (`--ui-perf-harness`)

A DEBUG-only harness that quantifies **UI responsiveness from the user's perspective**. It boots the real headed Avalonia app in an **isolated** appdata dir (your real Lumi data is never touched), seeds ~147 varied scenario chats in memory (tiny → huge, tool-heavy, markdown-heavy), then drives a catalog of real UX actions through the actual ViewModels/Views while a background probe continuously samples **UI-thread dispatcher latency** (at `Background` priority). High latency == the UI thread is busy / has little idle headroom == clicks/keystrokes/repaints would feel laggy or frozen. Samples are sliced per-action, aggregated into per-action and per-category stats, classified by impact (Good / Moderate / High / Critical), and ranked worst-first.

- **What it measures (and what it doesn't)**: the probe is a UI-thread **starvation proxy** — it detects when the UI thread is too busy to service work promptly, which correlates strongly with perceived lag and freezes. It does **not** directly measure input-to-pixel or GPU/render-thread time. Treat absolute milliseconds as **relative/regression** signal, not an exact UX latency SLA — especially in Debug builds, where JIT/no-optimization inflates numbers. Use it for ranking, trend tracking, and regression gating.
- **Self-contained & safe**: runs in its own process, seeds its own data in a temp appdata dir, skips onboarding, and **shuts itself down** when finished. It never sends real Copilot messages and never touches other Lumi instances. One failing action is logged and skipped — it never aborts the whole run.
- **Full mode** (default): runs every UX category — `--ui-perf-harness`.
- **Filtered mode**: limit to specific categories — `--ui-perf-harness --ui-perf-filter navigation,composer` (category names are matched case/separator-insensitively, e.g. `chat-open` == `Chat Open`). Categories: `Navigation`, `Chat open`, `Chat switch`, `Transcript scroll`, `Composer`, `Chat list`, `New chat`, `Search`.
- **Tuning flags**: `--ui-perf-iterations N` (default 6), `--ui-perf-warmup N` (default 2), `--ui-perf-sample-ms N` (probe interval, default 8), `--ui-perf-settle-ms N` (quiet window, default 120), `--ui-perf-keep-open` (leave the window open for inspection instead of self-shutdown), `--ui-perf-output <path>` (override JSON path). Aliases for the main flag: `--ui-responsiveness-harness`, `--stress-ui`, `--ui-perf`.
- **Regression gate**: `--ui-perf-fail-on <good|moderate|high|critical>` (aliases `--ui-perf-failon`, `--ui-perf-gate`) makes the process exit **3** if any action's impact is at or above the given level (exit **1** on harness error, **0** otherwise). Use this in CI to fail a build when a UX action regresses past a threshold.
- **Metrics**: latency stats are `p50/p95/p99/max` of probe samples within each action's window. `post(ms)` is the post-action drain time (UI-thread busy time *after* the action delegate returned, **excluding** the fixed quiet padding) — it is **not** a fixed floor. `IterationsWithStall` reports how many iterations hit a High-level stall, a consistency signal (a one-off spike vs. a reliable freeze).
- **Output**: a console report plus a JSON report at `%TEMP%\Lumi-ui-perf\report-<timestamp>.json` (with a `report-latest.json` copy). The JSON is the authoritative machine-readable artifact; use it to decide which UX section to optimize next. When a gate is set, `summary.gate` carries `{ level, failed, offenders }`.
- **Pure-logic core is unit-tested** in `tests/Lumi.Tests` (`UiResponsivenessMetricsTests`, `UiHarnessOptionsTests`, `UiResponsivenessReportTests`). The metrics/options/report types live in `src/Lumi/UiPerf/` and are always compiled; the probe/scenarios/harness are DEBUG-only and excluded from Release builds.


#### What to test and how

- **Did your control actually render?** Use `find_control` to search by name (`#MyControl`) or type (`Button`). If it's not found, something is wrong.
- **Are properties set correctly?** Use `get_control_properties` to check values, visibility, enabled state, dimensions — anything you set in XAML or code-behind.
- **Do bindings work?** Use `get_data_context` to check ViewModel state, and `get_binding_errors` to catch broken bindings. Binding errors are silent failures — always check.
- **Does interaction work?** Use `click_control` to press buttons, `input_text` to type into text fields, `set_property` to change values at runtime. Verify the app responds correctly.
- **Does it look right?** Use `take_screenshot` to capture the window or a specific control. Check layout, alignment, and visual appearance.
- **Is the tree structure correct?** Use `get_visual_tree` or `get_logical_tree` to verify parent-child relationships and nesting.
- **What's focused?** Use `get_focused_element` to check focus behavior after interactions.
- **Styles applied?** Use `get_applied_styles` to inspect CSS classes, pseudo-classes, and style setters on a control.

#### Control identifiers

Many tools take a `controlId` parameter. Three formats work:
- `#Name` — matches by `Name` property (e.g., `#SendButton`)
- `TypeName` — first control of that type (e.g., `TextBox`)
- `TypeName[n]` — nth control of that type, 0-indexed (e.g., `Button[2]`)

#### When to use it

- After adding or modifying any XAML or code-behind UI code
- After changing data bindings or ViewModel properties that affect the UI
- After styling changes — verify pseudo-classes and setters apply
- When debugging layout issues — inspect bounds, margins, and visibility
- When a feature "should work" but you're not sure — take a screenshot and see

## Showing UI Changes to the User

If the feature or fix you implement can be visibly seen by the user (e.g., layout changes, new controls, styling updates, new views), **keep the debug instance of Lumi running** after you finish — do not close it. In your message to the user, explain exactly where they should look to see the change (e.g., "Open the Agents tab and look at the top-right corner" or "Start a new chat and notice the updated welcome panel"). This lets the user immediately verify your work in the live app without having to relaunch it themselves.

## Key Conventions

- **Single JSON file persistence** — no database. New data collections go in `AppData` class in `Models.cs`
- **Chat transcript is built in code-behind** (`ChatView.axaml.cs`), not with data templates. New message types need a case in `AddMessageControl()`
- **System prompt assembly** — new context sources should extend `SystemPromptBuilder.Build()`
- **Tool display names** — add friendly mappings in `ChatView.axaml.cs` `GetFriendlyToolDisplay()` and `ChatViewModel.cs` `FormatToolDisplayName()`
- **CRUD ViewModels** follow identical master-detail pattern — `SelectedX`, `IsEditing`, `EditX` properties, `New/Edit/Save/Cancel/Delete` commands
- **Strata controls** — always use Strata UI components for chat elements. Inspect the StrataTheme source for API
- **Modify StrataTheme when needed** — if a UI change makes more sense as a StrataTheme feature or fix (new control, new property, style tweak, bug fix), go ahead and make the change directly in the `Strata/` submodule. Don't work around library limitations in Lumi when the right fix belongs in Strata.
- **No over-engineering** — this is a personal app, keep implementations simple and direct

## Cross-Platform Conventions (Windows / Linux / macOS)

Lumi targets `net*-windows` on Windows and `net11.0` on Linux/macOS (the csproj selects the TFM by host OS). The `WINDOWS` preprocessor symbol is defined **only** for the Windows TFM, so it is the discriminator for compile-time gating. When adding a feature that touches platform-specific APIs, follow these rules so Windows-only capabilities never break the build — or leak to the agent — on Linux/macOS:

- **Compile-gate Windows-only TYPES with `#if WINDOWS` + an `#else` stub.** If code references a type that only exists on the Windows TFM or a Windows-only package (WinRT `Windows.*`, `FlaUI`, `Microsoft.Web.WebView2`, `Microsoft.Toolkit.Uwp.Notifications`), wrap the real implementation in `#if WINDOWS` and provide an `#else` stub with the **identical public API** (no-op / "unsupported" returns) so cross-platform callers compile unchanged. Examples: `BrowserService`, `UIAutomationService`, `VoiceInputService`, `NotificationService` (toast), `BrowserCookieService` (WebView2/DPAPI).
- **Runtime-gate Windows-only BEHAVIOR with `OperatingSystem.IsWindows()`** when the types compile everywhere but only work on Windows (P/Invoke, `System.Drawing`, registry). Provide a real Linux/macOS branch where one exists (e.g. notifications use `notify-send`/`osascript`; autostart uses `.desktop`/`.plist`; shells use `bash`/`pwsh`). Examples: `FileIconHelper`, `GlobalHotkeyService`, `MainWindow.ApplyLaunchAtStartup`, `BackgroundJobService`.
- **Condition Windows-only NuGet packages** in `Lumi.csproj` on the TARGET platform of the TFM (not the host), e.g. `Condition="'$([MSBuild]::GetTargetPlatformIdentifier($(TargetFramework)))' == 'windows'"`.
- **Do NOT leak unusable capabilities to the agent.** This is the most important rule. A Windows-only feature must be gated in *three* places:
  1. **Tools** — register the tool only on Windows in `ChatViewModel.BuildCustomTools` (see the `OperatingSystem.IsWindows()` guards for browser/`ui_*` tools).
  2. **System prompt** — `SystemPromptBuilder.Build` is parameterized by `PromptPlatform`; add Windows-only guidance behind the `isWindows` branch and provide a cross-platform alternative. Never describe PowerShell/COM/winget/registry/UI-automation/the embedded browser on non-Windows.
  3. **UI** — hide Windows-only affordances by binding `IsVisible` to an `OperatingSystem.IsWindows()`-backed VM property. Examples: the composer mic button (Strata `CanVoice` + `IsVoiceAvailable`), and the Settings "Keyboard" / "Browser" groups (`IsGlobalHotkeyAvailable` / `IsEmbeddedBrowserAvailable`). Prefer adding a Strata property over hard-coding visibility in Lumi. Default the property to "shown" so Windows is unchanged.
- **Keyboard shortcuts must feel native per platform.** Handlers compare against the platform command modifier — Cmd (`KeyModifiers.Meta`) on macOS, Ctrl (`KeyModifiers.Control`) on Windows/Linux — so a single check stays correct everywhere (on Windows/Linux it is exactly the old `Control` check). Shortcut HINT text is adapted centrally by `Loc.AdaptKeyboardHint` (applied inside `Loc.Get`), which renders ⌘/⌥/⇧ on macOS only; for hints built outside the localization table (typed `Loc.*` properties, literal XAML), call `Loc.AdaptKeyboardHint` explicitly.
- **Keep `[SupportedOSPlatform("windows")]`** on Windows-only members to satisfy the CA1416 platform-compatibility analyzer.
- **Validate cross-platform changes:** build on Linux (WSL: `dotnet build src/Lumi/Lumi.csproj -c Debug` from a Linux host produces the `net11.0` TFM) and run the OS-aware prompt tests (`SystemPromptBuilderTests`, which assert no Windows-only leakage on Linux/macOS). CI (`pr-validation.yml`) builds on `windows-latest` (+ tests), `ubuntu-latest`, and `macos-latest`.
