# Lumi — Project Report

**Version:** 0.1.0  
**Date:** February 18, 2026  
**Status:** Initial implementation complete

---

## Summary

Lumi is a cross-platform desktop personal assistant powered by GitHub Copilot SDK. It provides an agentic chat interface with customizable agents, reusable skills, project-scoped conversations, and persistent user memory — all wrapped in a modern Avalonia UI using the custom StrataTheme design system.

## What Was Built

### Core Application (17 source files)

| Layer | Files | Description |
|-------|-------|-------------|
| **Entry** | `Program.cs`, `App.axaml(.cs)` | Avalonia startup, dependency wiring |
| **Models** | `Models.cs` | 8 domain entities: `Chat`, `ChatMessage`, `Project`, `Skill`, `LumiAgent`, `Memory`, `UserSettings`, `AppData` |
| **Services** | `CopilotService.cs`, `DataStore.cs`, `SystemPromptBuilder.cs` | Copilot SDK integration, JSON persistence, dynamic system prompt assembly |
| **ViewModels** | 6 files | Full MVVM layer with `MainViewModel` (root + nav + chat list), `ChatViewModel` (streaming chat), plus CRUD VMs for Agents, Projects, Skills, Settings |
| **Views** | 6 XAML + 6 code-behind | Complete UI with sidebar navigation, chat transcript, and management pages |

### Features Implemented

1. **Chat Interface**
   - Real-time streaming with typing indicators
   - Message history with tool call visualization
   - Reasoning/thinking token display
   - File attachment support (pick, display, send)
   - File reference chips for tool-created files
   - Retry/resend from any user message
   - Welcome screen with suggestion chips
   - Auto-generated chat titles from Copilot

2. **Agent System**
   - Create custom agents with name, description, icon, system prompt
   - Select active agent per chat session
   - Agent badge displayed in chat header
   - Agent context injected into system prompt

3. **Skills Management**
   - Create skills with name, description, icon, markdown content
   - Skills listed in system prompt for LLM awareness
   - Master-detail UI with search

4. **Projects**
   - Named project containers with custom instructions
   - Filter chat list by project
   - Project instructions injected into system prompt

5. **Memory System**
   - Persistent user memories stored across sessions
   - Memories included in system prompt context

6. **Onboarding Flow**
   - First-run name prompt with branded overlay
   - Personalized greeting after setup

7. **Settings**
   - User name, preferred model
   - Dark/light theme toggle
   - Compact density mode

8. **Navigation & UX**
   - Sidebar with grouped chat list (Today, Yesterday, Previous 7 Days, Older)
   - Chat search and project filtering
   - Context menu for rename/delete
   - Inline chat rename dialog
   - Bottom nav bar: Chat, Projects, Skills, Agents, Settings
   - Smooth page switching

9. **Copilot SDK Integration**
   - GitHub authentication via SDK
   - Session creation and resumption
   - Model selection from available models
   - Full event stream handling: messages, reasoning, tools, titles, errors
   - Abort/stop generation support

10. **Tool Call Display**
    - Grouped tool calls under collapsible "thinking" sections
    - Friendly tool name mapping (e.g., "web_fetch" → "Reading website")
    - Duration tracking per tool call
    - Intent labels from `report_intent` tool
    - Filtered internal tools (read_powershell, stop_powershell)

## Architecture Decisions

| Decision | Rationale |
|----------|-----------|
| Single `Models.cs` file | Small domain model, all entities are simple POCOs — no need to split |
| Programmatic chat transcript | Strata controls need fine-grained control for streaming updates, tool grouping, and dynamic status — data templates would be limiting |
| JSON file persistence | Personal app, simple data model — no database overhead needed |
| No dependency injection | Small app with clear dependency graph — constructor injection at `App` level is sufficient |
| Event-driven streaming | Copilot SDK uses event callbacks — mapping to ViewModel state via `Dispatcher.UIThread.Post` is the natural Avalonia pattern |
| StrataTheme external reference | UI library is developed in parallel — project reference keeps them in sync |

## Tech Stack

| Component | Version |
|-----------|---------|
| .NET | 10.0 |
| Avalonia UI | 11.3.12 |
| CommunityToolkit.Mvvm | 8.4.0 |
| GitHub.Copilot.SDK | 0.1.24 |
| StrataTheme | Source reference |

## File Inventory

```
e:\Git\Lumi\
├── .github/
│   └── copilot-instructions.md    — Copilot workspace instructions
├── .gitignore                     — Git ignore rules
├── AGENTS.md                      — Agent guidelines (open standard)
├── REPORT.md                      — This report
├── Lumi.slnx                     — Solution file
└── src/Lumi/
    ├── App.axaml                  — Application XAML (themes)
    ├── App.axaml.cs               — Bootstrap + dependency wiring
    ├── Lumi.csproj                — Project file
    ├── Program.cs                 — Entry point
    ├── Models/
    │   └── Models.cs              — All domain entities
    ├── Services/
    │   ├── CopilotService.cs      — GitHub Copilot SDK wrapper
    │   ├── DataStore.cs           — JSON persistence
    │   └── SystemPromptBuilder.cs — System prompt composition
    ├── ViewModels/
    │   ├── AgentsViewModel.cs     — Agent CRUD
    │   ├── ChatViewModel.cs       — Chat state + streaming
    │   ├── MainViewModel.cs       — Root VM + navigation
    │   ├── ProjectsViewModel.cs   — Project CRUD
    │   ├── SettingsViewModel.cs   — User settings
    │   └── SkillsViewModel.cs     — Skill CRUD
    └── Views/
        ├── AgentsView.axaml(.cs)  — Agent management UI
        ├── ChatView.axaml(.cs)    — Chat transcript UI
        ├── MainWindow.axaml(.cs)  — App shell + sidebar
        ├── ProjectsView.axaml(.cs)— Project management UI
        ├── SettingsView.axaml(.cs)— Settings page UI
        └── SkillsView.axaml(.cs)  — Skill management UI
```

## Known Limitations

- No test project — unit and integration tests should be added
- StrataTheme is an external dependency with a hardcoded relative path
- No keyboard shortcuts for common actions
- No export/import for data backup
- Memory extraction is not yet automated (requires manual or LLM-driven extraction logic)
