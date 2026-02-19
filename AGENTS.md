# Lumi — Agent Guidelines

## Project Summary

Lumi is a cross-platform Avalonia desktop app — a personal agentic assistant that can do anything. It is a chat application with a modern, intuitive UX that feels alive. Lumi's main interface is a chat interface powered by GitHub Copilot SDK as the agentic backend. Single-project solution with MVVM architecture using CommunityToolkit.Mvvm source generators.

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

- **Models** (`src/Lumi/Models/Models.cs`): All domain entities in one file — `Chat`, `ChatMessage`, `Project`, `Skill`, `LumiAgent`, `Memory`, `UserSettings`, `AppData`
- **Services** (`src/Lumi/Services/`): `CopilotService` (SDK wrapper with streaming events), `DataStore` (JSON persistence to `%AppData%/Lumi/data.json`), `SystemPromptBuilder` (composite system prompt)
- **ViewModels** (`src/Lumi/ViewModels/`): `MainViewModel` (root), `ChatViewModel` (streaming chat), `AgentsViewModel`, `ProjectsViewModel`, `SkillsViewModel`, `SettingsViewModel` — all CRUD follows same pattern
- **Views** (`src/Lumi/Views/`): Avalonia XAML + code-behind. `ChatView.axaml.cs` is the heaviest — builds transcript programmatically using Strata controls
- **External dependency**: StrataTheme UI library at `../../../Strata/src/StrataTheme/` — provides `StrataChatShell`, `StrataChatMessage`, `StrataMarkdown`, `StrataThink`, `StrataAiToolCall`, etc.

## Code Style

- C# with nullable reference types enabled, implicit usings
- `[ObservableProperty]` on fields (e.g., `string _name;` → generates `Name` property)
- `[RelayCommand]` on methods for bindable commands
- `partial void On<PropertyName>Changed()` for property change side effects
- All Copilot event handlers must dispatch to UI thread via `Dispatcher.UIThread.Post()`

## Build & Test

```bash
dotnet build src/Lumi/Lumi.csproj
cd src/Lumi && dotnet run
```

No test project exists yet. StrataTheme project must be available at `E:\Git\Strata`.

## Key Conventions

- **Single JSON file persistence** — no database. New data collections go in `AppData` class in `Models.cs`
- **Chat transcript is built in code-behind** (`ChatView.axaml.cs`), not with data templates. New message types need a case in `AddMessageControl()`
- **System prompt assembly** — new context sources should extend `SystemPromptBuilder.Build()`
- **Tool display names** — add friendly mappings in `ChatView.axaml.cs` `GetFriendlyToolDisplay()` and `ChatViewModel.cs` `FormatToolDisplayName()`
- **CRUD ViewModels** follow identical master-detail pattern — `SelectedX`, `IsEditing`, `EditX` properties, `New/Edit/Save/Cancel/Delete` commands
- **Strata controls** — always use Strata UI components for chat elements. Inspect the StrataTheme source for API
- **Modify StrataTheme when needed** — if a UI change makes more sense as a StrataTheme feature or fix (new control, new property, style tweak, bug fix), go ahead and make the change directly in the StrataTheme project at `E:\Git\Strata`. Don't work around library limitations in Lumi when the right fix belongs in Strata.
- **No over-engineering** — this is a personal app, keep implementations simple and direct
