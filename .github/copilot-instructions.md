# Copilot Instructions for Lumi

## Project Overview

Lumi is a cross-platform Avalonia desktop app — a personal agentic assistant that can do anything. It is a chat application with a modern, intuitive UX that feels alive. Lumi's main interface is a chat interface powered by GitHub Copilot SDK as the agentic backend.

## Core Concepts

Lumi is built around these domain concepts:

- **Chat** — A chat session with Lumi. The primary interaction surface. Each chat has a message history and can be linked to a project and/or agent.
- **Project** — A named collection of chats with specific custom instructions. Projects scope conversations and inject their instructions into the system prompt so Lumi understands the context.
- **Skill** — A reusable capability definition written in markdown. Skills explain how to do a specific task (e.g., "Word creator" takes markdown and converts it to Word using Python). Skills are listed in the system prompt so the LLM knows what's available.
- **Lumis (Agents)** — Custom agent personas. Each agent is a combination of a system prompt, skills, and tools for a specific scenario (e.g., "Daily Lumi" looks at mail, todo actions, and helps plan the day). Users create agents and select them in the agents tab to talk with them.
- **Memory** — Lumi keeps persistent memories of important information extracted from conversations. Memories are included in the system prompt across all sessions so Lumi remembers the user over time.

## User Flows

- **Onboarding** — On first launch, Lumi asks the user what it should call them. After that, it greets them personally.
- **Chat interaction** — The user talks to Lumi in a streaming chat interface. Lumi shows what it's doing (tool calls, reasoning, typing) in an intuitive way.
- **Memories** — As the user interacts, Lumi keeps memories of important information from conversations for future context.
- **Skills** — Users create skills and can reference them through the chat box in any chat. Skills are surfaced in the system prompt.
- **Agents** — Users create custom agents and select them in the agents tab to talk with specialized personas.
- **Projects** — Users organize chats into projects with custom instructions that shape Lumi's behavior.
- **Context awareness** — Lumi takes into account the current context: active project, active agent, time of day, user name, skills, and memories. All of this is assembled into the system prompt by `SystemPromptBuilder`.

## UX Design Principles

- **Modern and alive** — Components are animated, interactions feel responsive. The app uses StrataTheme for a polished design system.
- **Not bloated** — The main interface focuses on chats. It's easy to find elements and reach them without clutter.
- **Welcome experience** — The main interface welcomes users to send a message with an elegant welcome panel and suggestion chips.
- **Easy switching** — It's easy to switch between agents and use skills from the chat interface.
- **Transparency** — Lumi shows what it's doing: tool calls are grouped and labeled with friendly names, reasoning tokens are displayed, and streaming indicators keep the user informed.
- **Dedicated management** — Agents, skills, and projects each have dedicated sections with master-detail CRUD interfaces and search.

## Tech Stack

- **.NET 10** with C# and nullable reference types
- **Avalonia UI 11.3** — cross-platform desktop framework
- **CommunityToolkit.Mvvm 8.4** — MVVM source generators (`[ObservableProperty]`, `[RelayCommand]`)
- **GitHub.Copilot.SDK** — agentic backend for LLM interaction
- **StrataTheme** — custom UI component library (external project reference at `../../../Strata/src/StrataTheme/`)

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

## Coding Guidelines

1. **Use CommunityToolkit.Mvvm patterns** — `[ObservableProperty]` generates properties from fields (e.g., `[ObservableProperty] string _name;` generates `Name`). Use `partial void On<PropertyName>Changed()` for side effects.
2. **UI thread safety** — All CopilotService event handlers dispatch to UI thread via `Dispatcher.UIThread.Post()`.
3. **Strata controls** — When adding chat UI elements, use Strata controls. Reference the StrataTheme source at `E:\Git\Strata` for API details.
4. **Modify StrataTheme when needed** — If a UI change makes more sense as a StrataTheme feature or fix (new control, new property, style tweak, bug fix), go ahead and make the change directly in the StrataTheme project at `E:\Git\Strata`. Don't work around library limitations in Lumi when the right fix belongs in Strata.
5. **No database** — Data is stored as JSON. Add new persistent collections to `AppData` in `Models.cs`.
6. **System prompt context** — When adding new context sources, extend `SystemPromptBuilder.Build()`.
7. **Chat transcript** — The chat view builds controls programmatically in `ChatView.axaml.cs`. New message types need a rendering case in `AddMessageControl()`.
8. **Keep it simple** — Avoid over-engineering. This is a personal assistant app, not an enterprise system.

## Building & Running

```bash
dotnet build src/Lumi/Lumi.csproj
cd src/Lumi && dotnet run
```

Requires the StrataTheme project at `E:\Git\Strata` (or adjust project reference).
