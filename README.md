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

### Desktop motion

The composer keeps its thin Stratum underline, drawing it from left to right on focus and
showing a visible travelling highlight while focused. Newly sent messages and live assistant
replies rise into place once, without moving transcript layout or replaying on
history rebuilds, paging, or chat switches. Section navigation and the coding strip use the
same short, interruptible motion, and the welcome-to-chat handoff has no delayed fade.
The existing **Show Animations** setting disables the new message/section entrances and
composer focus motion; the static underline remains visible.

### Mobile conversations

Announced files appear as readable attachment cards after the reply for their turn,
including file-only replies, without changing transcript paging or download permissions.
New chats show the desktop-resolved reasoning effort and context-window selection for
the chosen model. Older desktop hosts remain compatible and show **Set by desktop**
when they do not provide that metadata; unsupported settings are labelled explicitly.
If backgrounding interrupts a send acknowledgement, resume checks for the original
request ID in the desktop transcript before clearing its draft or adopting its new chat.
This confirmation is read-only: it does not resend the message, and newer draft edits
are preserved. Android uses the managed HTTP transport so stopping a live connection
does not leave foreground reconnection waiting on a native streaming read.

The PWA measures its actual dynamic-height canvas when applying keyboard overlap, so
browser resizing does not add a second bottom gap. Home-indicator safe space is retained.

### Lumi tool availability

Lumi preloads its own tool definitions for new, resumed, and helper sessions,
including background-job chats. This bypasses Copilot's deferred-tool discovery
state getting stuck after an earlier successful lookup. The policy is applied
centrally when building session configurations, after platform checks and agent
tool restrictions. Tool names, schemas, handlers, and permission behavior are
unchanged.

External MCP tools retain Copilot's existing loading policy; tool search is not
globally disabled. Preloading Lumi's tools adds their schemas to the model's
context, trading some prompt size for reliable access. Website sign-in
requirements are unchanged.

### Private mobile web app with Microsoft Dev Tunnels

The existing Avalonia mobile PWA also works over an **owner-only Microsoft Dev
Tunnel**, without Tailscale on the phone. In **Settings > Mobile**, turn on phone
access and select **Microsoft Dev Tunnel**, then **Web app**.

Setup presents the three connection methods as matching choices: Tailscale,
Dev Tunnel (Microsoft sign-in), and local Wi-Fi. They share one row on wider
windows and all stack together on narrow windows. Each choice identifies its
supported apps; Dev Tunnel explains the web-only restriction, and Retry appears
only when a private tunnel actually needs attention.

Desktop setup is guided after selecting **Dev Tunnel**:

1. Lumi reuses the official CLI in its portable `tools\devtunnel` subdirectory
   or on `PATH`. If none is installed, it first asks **Install Microsoft Dev
   Tunnel?**, with **Install & continue** and **Cancel**. No download starts
   without approval. Cancel or dismiss the dialog to leave it uninstalled;
   Retry asks again. Existing installations skip this prompt.
   After approval, the matching Microsoft binary is downloaded into
   `tools\devtunnel` under Lumi's user-data directory. No administrator access,
   system installation, or PATH changes are needed.
2. If Microsoft sign-in is missing, Lumi opens the CLI's Microsoft browser
   sign-in flow. Complete sign-in with the account you want to use on the phone.
   Personal Microsoft and Microsoft Entra accounts work; GitHub authentication
   is not accepted for this connection.
3. Setup continues automatically and displays the verified account. Choose
   **Web app** to get the HTTPS link and QR code.
4. Open that link on your phone, sign in with **the same account**, and enter the
   separate, single-use Lumi pairing code displayed on the PC.

Downloads use fixed HTTPS URLs from Microsoft's official Dev Tunnel distribution
account, with redirects disabled, size/time limits, and an executable-format check.
On Windows, a valid Microsoft Authenticode signature is also required before the
download is installed or executed. Other platforms authenticate the distribution
through Microsoft's HTTPS endpoint; they do not claim Windows signature verification.
The executable is published only after verification; canceled or failed downloads
are discarded. Turning off phone access cancels download/sign-in, without closing
the user's browser. Credentials remain in the CLI's standard platform credential
store, not Lumi settings. Existing installed versions are not silently upgraded.
Automatic acquisition supports Microsoft's Windows x64 binary (including Windows
ARM64 emulation), macOS x64/ARM64, and Linux x64/ARM64 binaries. Linux still needs
the system credential-store dependencies required by Microsoft's CLI (such as
`libsecret`); Lumi does not install system packages with administrator privileges.
For other systems, an existing CLI on `PATH` remains usable.

Lumi creates its own new tunnel and verifies that **both the tunnel and its port
have no additional access grants before hosting**. It never enables anonymous,
organization/tenant-wide, or shared-token access, and never reuses an existing
tunnel with unknown permissions. All PWA assets and API routes are behind
Microsoft's sign-in gate; paired-device bearer authentication, expiry/attempt
limits on pairing, and device revocation remain in force.

The PWA manifest link uses `crossorigin="use-credentials"` so Edge/Chromium
includes the signed-in Microsoft session when checking installability. Browsers
otherwise fetch even a same-origin manifest without cookies and receive a sign-in
page instead of the manifest. No manifest or icon is made public for installation.

When Microsoft sign-in returns a top-level browser navigation to a Lumi API URL,
the server sends the browser to `/app/` instead of displaying the raw pairing
error. Only GET document navigations receive that handoff; API fetches, streams,
and commands still require the paired-device token, and the tunnel/network
checks still run first.

The PWA uses the Android app's launcher artwork, with separate normal and opaque
maskable icons so Android does not crop a transparent rounded-square icon as if
it were full-bleed artwork. Installed launchers can cache old icons; remove the
old PWA shortcut and install again from the current `/app/` link if the icon has
not refreshed after updating Lumi.

This mode binds Lumi's listener to **127.0.0.1 only**, disables LAN discovery, and
does not fall back to LAN or Tailscale if setup or hosting fails. Browser requests
are restricted to their original origin and do not follow authentication
redirects with Lumi credentials. Host-header validation is not relaxed to allow
arbitrary public hostnames.

The URL is reachable at Microsoft's public gateway, but Lumi is **not publicly
accessible**: Microsoft authenticates and authorizes the tunnel owner first.
HTTPS terminates at Microsoft's gateway and the relay connection to the PC is
encrypted; this is not Tailscale's device-to-device WireGuard trust model.
Do not manually broaden the managed tunnel's access rules or issue/share tunnel
access tokens. Keep the PC and Lumi running. Turning off phone access or switching
transport stops the owned relay and removes its tunnel; unused tunnel resources
expire after one day if cleanup cannot reach Microsoft. Restarting creates a new
link, which also requires fresh browser pairing for that origin.

Dev Tunnels is a Microsoft preview service without a production SLA. This mode
is for the **PWA**, not the native Android transport. Existing Tailscale and
explicit local-network connections are unchanged and remain available separately.
Developer builds need the browser assets published to the desktop executable's
`remote-web` directory (or `LUMI_REMOTE_WEB_ROOT`); release packages include them.

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

The composer's real-pixel animation checks run in an isolated Skia test process;
the ordinary drawing-free headless suite skips them to avoid mixing graphics backends:

```powershell
$env:LUMI_COMPOSER_MOTION_RENDER = "1"
try {
    dotnet test tests\Lumi.Tests\Lumi.Tests.csproj --filter "FullyQualifiedName~ComposerFocusLine_UsesVisibleMotionWithRealThemeBrushes"
} finally {
    Remove-Item Env:\LUMI_COMPOSER_MOTION_RENDER
}
```

### Windows Installer

Windows releases use the standard Velopack installer with the branded
`src\Lumi\Assets\installer-splash.png` image, configured through `--splashImage`
in the release workflow. Velopack displays progress and launches Lumi when
installation completes. This is presentation-only: installer locking, repair,
command-line options, code signing, and automatic updates are unchanged.

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
