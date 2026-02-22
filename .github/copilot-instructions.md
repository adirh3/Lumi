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
3. **Strata controls** — When adding chat UI elements, use Strata controls. Reference the StrataTheme source for API details.
4. **Modify StrataTheme when needed** — If a UI change makes more sense as a StrataTheme feature or fix (new control, new property, style tweak, bug fix), go ahead and make the change directly in the StrataTheme project. Don't work around library limitations in Lumi when the right fix belongs in Strata.

> **CRITICAL: StrataTheme file locations** — There are TWO copies of StrataTheme. The csproj resolves `StrataPath` with two conditions: first it checks `../../../Strata/src/StrataTheme/` (a sibling repo next to the Lumi repo root — this is the **primary** copy used by the build), then falls back to `../../Strata/src/StrataTheme/` (a git submodule inside the Lumi repo at `Strata/`). The submodule copy is **stale and may be outdated**. **Always read and edit Strata files in the primary external repo** (the one resolved by the first condition), never in the `Strata/` submodule directory inside this repo. Editing the wrong copy will silently have no effect. To find the active path, check which `StrataPath` condition matches in `src/Lumi/Lumi.csproj`.
5. **No database** — Data is stored as JSON. Add new persistent collections to `AppData` in `Models.cs`.
6. **System prompt context** — When adding new context sources, extend `SystemPromptBuilder.Build()`.
7. **Chat transcript** — The chat view builds controls programmatically in `ChatView.axaml.cs`. New message types need a rendering case in `AddMessageControl()`.
8. **Keep it simple** — Avoid over-engineering. This is a personal assistant app, not an enterprise system.

## Building & Running

```bash
dotnet build src/Lumi/Lumi.csproj
cd src/Lumi && dotnet run
```

Requires the StrataTheme project as a sibling repo (see `StrataPath` in `Lumi.csproj`).

## UI Testing with Avalonia MCP

Lumi has an Avalonia MCP server configured in `.vscode/mcp.json`. This gives you live access to the running app — you can see the UI, click buttons, type text, inspect controls, check bindings, and take screenshots. **Use it.**

**Every time you make a UI change, you must test it with the MCP tools.** Don't just build and hope it works — run the app, poke at it, and confirm your changes look and behave correctly. This is your primary way of verifying UI work since there are no UI tests.

### Workflow

1. Run `dotnet tool restore` once to ensure the CLI tool is available
2. Start Lumi: `cd src/Lumi && dotnet run`
3. Use the MCP tools to verify your work

### What to test and how

- **Did your control actually render?** Use `find_control` to search by name (`#MyControl`) or type (`Button`). If it's not found, something is wrong.
- **Are properties set correctly?** Use `get_control_properties` to check values, visibility, enabled state, dimensions — anything you set in XAML or code-behind.
- **Do bindings work?** Use `get_data_context` to check ViewModel state, and `get_binding_errors` to catch broken bindings. Binding errors are silent failures — always check.
- **Does interaction work?** Use `click_control` to press buttons, `input_text` to type into text fields, `set_property` to change values at runtime. Verify the app responds correctly.
- **Does it look right?** Use `take_screenshot` to capture the window or a specific control. Check layout, alignment, and visual appearance.
- **Is the tree structure correct?** Use `get_visual_tree` or `get_logical_tree` to verify parent-child relationships and nesting.
- **What's focused?** Use `get_focused_element` to check focus behavior after interactions.
- **Styles applied?** Use `get_applied_styles` to inspect CSS classes, pseudo-classes, and style setters on a control.

### Control identifiers

Many tools take a `controlId` parameter. Three formats work:
- `#Name` — matches by `Name` property (e.g., `#SendButton`)
- `TypeName` — first control of that type (e.g., `TextBox`)
- `TypeName[n]` — nth control of that type, 0-indexed (e.g., `Button[2]`)

### When to use it

- After adding or modifying any XAML or code-behind UI code
- After changing data bindings or ViewModel properties that affect the UI
- After styling changes — verify pseudo-classes and setters apply
- When debugging layout issues — inspect bounds, margins, and visibility
- When a feature "should work" but you're not sure — take a screenshot and see
