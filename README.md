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

### Lazy MCP initialization

In **Settings > AI & Models > MCP Servers**, enable **Fast MCP Initialization**, then
**Lazy MCP Initialization** (off by default). Changes apply to new sessions; existing
sessions keep their configuration.

The first connection starts the real local MCP server and learns its initialization
metadata and complete tool definitions. Saved catalogs have **no age expiry**.
Later connections can use an older, last-known snapshot: the proxy answers
`initialize`, ordinary `tools/list`, and `ping` without starting that server.
The first real operation, such as `tools/call`, activates or joins the shared
backend and checks live discovery against the catalog that client saw before
forwarding the operation. Each client keeps its own stable discovery view, even
when clients share one running backend. Tool results are never cached, and tool
calls are not replayed after an uncertain failure.

Use **Refresh MCP Tools > Refresh catalogs** in the same settings group to clear
stored catalog snapshots and mark current chats for safe reconnect on their next
turn, preserving their session history and preferences. Active work finishes
normally: refresh does not force an interruption or kill a busy backend.
Ordinary last-owner cleanup may still stop a backend as usual. Refresh is an
explicit user action, never a timer; catalogs are rediscovered when chats reconnect,
not eagerly by the button itself.

This is deliberately conservative:

- Only tools-only stdio servers with a supported protocol version and a recognized
  client initialization are eligible. Notification-capable SDK tool servers are
  accepted: the proxy advertises `tools.listChanged: false` for its stable client
  view, rather than promising live tool-list updates. Use explicit refresh to
  request a new catalog.
- Remote HTTP servers, resources/prompts/other unsupported capability kinds, roots,
  and unknown client extensions retain eager initialization in this iteration.
  Copilot's advertised sampling and MCP Apps/task support are accepted when warm-up
  succeeds without callbacks; advertising client support does not mean a
  tools-only server uses it.
- Only successful, complete, unpaginated `tools/list` responses to absent/empty
  parameters (or an optional progress-correlation token) are cached. Backend
  pagination remains eager; cursors are not reused across sessions.
- Before a cached tool call is forwarded, validation checks the selected tool's
  **full advertised contract**, server name, negotiated protocol, and instructions.
  A server version change, tool-list reordering, or unrelated tool additions alone
  do not block that call. If validation fails, the operation is not forwarded;
  refresh and reconnect to rediscover the server.
- MCP does **not** promise cross-run catalog stability. Package dependencies,
  external sign-in state, and remote configuration can change without changing the
  launch configuration. Descriptions can therefore remain stale until the server
  is needed or an explicit refresh is requested; reuse is not a freshness guarantee.
- Cache keys include the configuration, working directory, effective launch
  environment, client initialization profile, and identifiable executable/script
  file stamps. Cache files contain server discovery metadata, not configuration
  credentials or tool-call arguments/results, in Lumi's `mcp-discovery` app-data
  directory. Missing, corrupt, oversized, or unreadable cache data falls back to real
  discovery. Storage failures are logged without failing a successful MCP response.

Current 2025 protocol versions are tested; newer/unknown protocol versions remain
unsupported by lazy discovery. This is not a claim of full MCP 2026 support.
The proxy rejects the newer `server/discover` request with method-not-found without
starting or binding a backend, so Copilot can fall back to the supported
`initialize` handshake with its actual client profile.
Servers requiring bidirectional client callbacks still need direct Copilot
connections (turn off Fast MCP Initialization); lazy mode does not add live
callback forwarding.

Package-launcher text on stdout (for example, NuGet credential-provider startup
messages) is logged and retained as redacted diagnostics rather than failing the
MCP connection. Malformed JSON-RPC, process exits, and initialization timeouts still
fail explicitly; startup failures include the recent captured output.

## Tech Stack

- **.NET 11** with C#
- **Avalonia UI 12.0.4** — cross-platform desktop framework
- **CommunityToolkit.Mvvm 8.4** — MVVM source generators
- **GitHub Copilot SDK** — agentic LLM backend
- **[StrataTheme](https://github.com/adirh3/Strata)** — custom UI component library

## Getting Started

### Prerequisites

- [.NET 11 SDK](https://dotnet.microsoft.com/download)

### Clone

```bash
git clone --recurse-submodules https://github.com/adirh3/Lumi.git
cd Lumi
```

> **Note:** The `--recurse-submodules` flag pulls the [StrataTheme](https://github.com/adirh3/Strata) UI library.

If you already cloned without submodules:

```bash
git submodule update --init --recursive
```

### Build & Run

The Copilot SDK is a normal NuGet dependency. `NuGet.Config` restores the versioned,
unofficial `Lumi.Copilot.SDK` package from the checked-in `vendor/nuget` feed; all
other packages come from NuGet.org. No SDK submodule, source build, patch step, feed
credentials, or Node.js installation is needed to build Lumi.

The package preserves native in-memory skill loading while the generic provider API
is proposed upstream in [github/copilot-sdk#2672](https://github.com/github/copilot-sdk/pull/2672).
See [package provenance](vendor/nuget/README.md) for the exact source commit and checksum.
Lumi's skill storage and editing behavior are unchanged.

```bash
dotnet build src/Lumi/Lumi.csproj
cd src/Lumi && dotnet run
```

### Linux Release Packages

The auto-updating Linux release is an AppImage. Make it executable before launching:

```bash
chmod +x Lumi-*.AppImage
./Lumi-*.AppImage
```

Ubuntu 24.04 requires `libfuse2t64` for AppImages (`libfuse2` on older Ubuntu releases).
If FUSE is unavailable, use the release's `linux-x64-portable.tar.gz` archive instead:

```bash
tar -xzf Lumi-*-linux-x64-portable.tar.gz
chmod +x Lumi
./Lumi
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

The local MCP proxy is organized by responsibility:

- `McpProxyRuntime` owns HTTP routing and registration; its `.Registration` partial contains leases.
- `McpStdioServerConnection` owns backend lifecycle; `.Discovery`, `.Process`, and `.Transport` keep catalog validation, process management, and message forwarding separate.
- `McpDiscoveryCache` stores snapshots, while `McpDiscoverySession` holds each client's advertised contract.
- `JsonRpc` contains the shared message-format helpers.

## License

[MIT](LICENSE)
