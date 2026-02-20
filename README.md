# Lumi ✨

A personal agentic desktop assistant powered by [GitHub Copilot SDK](https://github.com/features/copilot) and [Avalonia UI](https://avaloniaui.net/). Lumi is a cross-platform chat application with a modern, intuitive UX that feels alive.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

## Features

- **Streaming chat** — Real-time streamed responses with tool call visualization, reasoning display, and typing indicators
- **Agents (Lumis)** — Create custom agent personas with their own system prompts, skills, and tools
- **Skills** — Reusable capability definitions in markdown that teach the assistant new abilities
- **Projects** — Organize chats with custom instructions that shape Lumi's behavior
- **Memories** — Persistent facts extracted from conversations, remembered across all sessions
- **Context awareness** — Lumi assembles context from the active project, agent, time of day, user name, skills, and memories into every interaction
- **System tray** — Minimize to tray with global hotkey for instant access
- **Charts** — Inline interactive charts (line, bar, donut, pie) rendered in chat
- **Localization** — English and Hebrew, with easy extension to other languages
- **Desktop notifications** — Toast notifications when responses complete in the background

## Tech Stack

- **.NET 10** with C#
- **Avalonia UI 11.3** — cross-platform desktop framework
- **CommunityToolkit.Mvvm 8.4** — MVVM source generators
- **GitHub Copilot SDK** — agentic LLM backend
- **[StrataTheme](https://github.com/adirh3/Strata)** — custom UI component library

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

### Clone

```bash
git clone --recurse-submodules https://github.com/adirh3/Lumi.git
cd Lumi
```

> **Note:** The `--recurse-submodules` flag is required to pull the [StrataTheme](https://github.com/adirh3/Strata) UI library.

If you already cloned without submodules:

```bash
git submodule update --init --recursive
```

### Build & Run

```bash
dotnet build src/Lumi/Lumi.csproj
cd src/Lumi && dotnet run
```

### Local StrataTheme Development

If you have the [Strata](https://github.com/adirh3/Strata) repo cloned as a sibling directory (i.e., `../Strata/` relative to this repo), the build automatically uses your local copy instead of the submodule. No configuration needed — just clone both repos side by side:

```
Git/
├── Lumi/      ← this repo
└── Strata/    ← local Strata clone (optional, auto-detected)
```

## Architecture

```
src/Lumi/
├── Models/          — Domain entities (Chat, Project, Skill, Agent, Memory)
├── Services/        — CopilotService, DataStore (JSON), SystemPromptBuilder
├── ViewModels/      — MVVM ViewModels with CommunityToolkit.Mvvm generators
└── Views/           — Avalonia XAML views + code-behind
```

Data is persisted as a single JSON file in `%AppData%/Lumi/data.json` — no database required.

## License

[MIT](LICENSE)
