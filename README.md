# Lumi ✨

A personal agentic desktop assistant powered by [GitHub Copilot SDK](https://github.com/features/copilot) and [Avalonia UI](https://avaloniaui.net/). Lumi is a cross-platform chat application with a modern, intuitive UX that feels alive.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

## Features

- **Streaming chat** — Real-time streamed responses with tool call visualization, reasoning display, and typing indicators
- **Reply to** — Ask about a specific part of an answer: select any words in Lumi's reply and tap the floating **Reply** pill (or hover a message and choose **Reply**). The quote rides along above your message, and clicking it jumps back to the highlighted source
- **Agents (Lumis)** — Create custom agent personas with their own system prompts, skills, and tools
- **Skills** — Reusable capability definitions in markdown that teach the assistant new abilities
- **Sharing** — Share skills, Lumis and MCP servers as plain-text files anyone can read, or as a compact code for Teams/Slack that Lumi picks up from the clipboard; import them (or any `SKILL.md` / MCP config) with a receipt of exactly what gets added
- **Projects** — Organize chats with custom instructions that shape Lumi's behavior
- **Memories** — Persistent facts extracted from conversations, remembered across all sessions
- **Context awareness** — Lumi assembles context from the active project, agent, time of day, user name, skills, and memories into every interaction
- **System tray** — Minimize to tray with global hotkey for instant access
- **Charts** — Inline interactive charts (line, bar, donut, pie) rendered in chat
- **Localization** — English and Hebrew, with easy extension to other languages
- **Desktop notifications** — Toast notifications when responses complete in the background

### Sharing skills, Lumis and MCP servers

**Share** (editor header, or right-click an item) opens a card with a receipt of what leaves the
computer. A skill is shared as a standard Agent Skills `SKILL.md` (slug `name`, Lumi's display name
and icon under `metadata`), so it works in Claude Code, Codex, Cursor and Gemini CLI; **Send to**
installs it as `<skills folder>/<name>/SKILL.md` for those tools when they are present, and
**Save to folder** writes the same layout anywhere (for example a repo's `.github/skills`). A Lumi or
MCP server becomes a `*.lumi.md` capability pack: readable markdown whose tagged code blocks carry the
Lumi, its skills and its MCP servers (as a standard `mcpServers` config).

Secrets never travel: environment variable and header values are left out (only their names are
shared), and credentials inlined into arguments or URLs are replaced with `<REDACTED>`. **Import**
(sidebar, empty state, or drop a file on the Skills, Lumis or MCP Servers page) accepts Lumi packs,
any `SKILL.md` or skill folder, and MCP JSON from Claude Desktop, VS Code or a README. It shows a
receipt first — the exact commands and URLs, the keys you will need to add, and anything reused or
renamed — and adds nothing until you confirm. Imported MCP servers arrive turned off with empty
values, invisible characters that could hide instructions are removed, and existing items are never
overwritten.

**Copy for chat** (editor header, share card, or right-click) is the quickest way to hand something to
a teammate in Teams, Slack or any chat. It copies a short message with a *Lumi code*: `lumi1.` followed
by the same redacted text, Brotli-compressed and base64url-encoded behind a 4-byte SHA-256 check, in a
code block (rich HTML for editors that support it, a fenced block for everything else). Codes are about
half the size of the text, survive wrapping and chat formatting, and a code that was cut short is
reported as damaged instead of being half-imported. When the teammate copies it and switches back to
Lumi, a notice offers **Preview**, which opens the same import receipt. Lumi only reads the clipboard
when its window is activated, only looks for its own codes and packs, never offers your own copies or
things you already have, and can be turned off under **Settings › General › Sharing** (off by default
on macOS, which warns when apps read the clipboard).

### Unread chats

Right-click a sidebar chat and choose **Mark as unread** to add it to the unread
inbox without leaving your current conversation. For unread chats, the menu
instead offers **Mark as read**, which clears only that chat's mark without
opening it. Closed/open envelope icons distinguish the two actions. Opening the
chat or choosing **Mark all as read** also clears the mark. Like automatic unread
indicators, these marks last for the current app session.

### Resuming chats after backend history errors

For `400 input item ID does not belong to this connection`, click **Try again** or send
your next message normally. Lumi replaces the unusable backend session and restores
context from the saved user, assistant, and system messages as text. The visible
transcript is kept; native tool/image history is not replayed. This also repairs
chats whose error was saved by an older Lumi version as a same-session retry, and
works for background and remote sends without first opening the chat. Unrelated
errors retain their existing retry behavior; authentication and quota failures
still require their normal resolution.

### Desktop motion

The composer keeps its thin Stratum underline, drawing it from left to right on focus and
then showing a travelling highlight for two seconds before resting. Refocusing replays it.
Newly sent messages and live assistant
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
Release PWA publishes ahead-of-time compiled WebAssembly. The mobile transcript realizes
only turns near the viewport while retaining the full loaded page and offscreen streaming
updates, avoiding eager layout of every markdown answer when opening a conversation.

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
unofficial `Lumi.Copilot.SDK` package from the checked-in `vendor/nuget` feed.
Other packages except the patched Avalonia core come from NuGet.org. No SDK
submodule, source build, patch step, feed credentials, or Node.js installation
is needed to build Lumi.

The package preserves native in-memory skill loading while the generic provider API
is proposed upstream in [github/copilot-sdk#2672](https://github.com/github/copilot-sdk/pull/2672).
See [package provenance](vendor/nuget/README.md) for the exact source commit and checksum.
Lumi's skill storage and editing behavior are unchanged.

`Directory.Build.targets` pins the core `Avalonia` package to the vendored
`12.1.2.2` build throughout the project-reference graph, including Strata. It fixes
input-method notifications exposing partially updated selections and empty
selection geometry reaching the text-line renderer. Official Avalonia peer
packages remain at `12.1.2`; no Strata input guard or runtime patch is used.
See [Avalonia patch provenance and reproduction](vendor/avalonia/12.1.2.2/README.md).
When switching an existing build to the patched package, use a clean rebuild
(`dotnet build src/Lumi/Lumi.csproj -t:Rebuild`) or a fresh `--artifacts-path`.

**GitHub sign-in:** The custom SDK package supplies one checksum-pinned, matching
official full Copilot CLI during build and publish. Lumi needs no separate CLI
download targets. The SDK uses it over stdio, and GitHub's `copilot login` handles browser/device authorization.
The current full CLI does not expose `logout`, so Lumi uses the SDK account API
to remove only the selected stored user. Existing credential selection and
storage are unchanged; AI Models refreshes the shared sign-in display.
No additional CLI, OAuth app, token store, or Node.js installation is required.
See [package provenance and reproduction](vendor/nuget/README.md).

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

### Windows computer-use automation

Windows desktop tools use native UI Automation patterns, cached UI properties,
and provider-side search. By default, `ui_inspect` returns relevant visible
controls at every depth, with actions before long text and stable control numbers.
Deep layout wrappers therefore do not hide an app's navigation or primary action.
Use `compact=false` with `depth` for a hierarchical layout/debugging tree.
`ui_find` searches a specific name, `id:AutomationId`, or `type:ControlType`.
Window titles must be unique; inspect a named app directly instead of listing all
windows first. `ui_list_windows` also supplies explicit `hwnd:0x...` selectors and
minimized state for windows with the same title. Inspection, screenshots, and
batches restore minimized targets with `SW_SHOWNOACTIVATE`; routine window state
is recovered automatically without asking the user to restore it manually.

Desktop actions are **background-first**: native value, invoke/default-action,
toggle, selection, and scroll patterns do not deliberately activate windows or
move the pointer. Some applications may still raise their own dialogs. A control
that needs physical input reports that foreground permission is required instead
of silently interrupting the user. `ui_press_keys` and keyboard steps require
`allowForeground=true`; pointer-only fallbacks require the same explicit opt-in.

Prefer `ui_do` for a known sequence rather than a separate model turn per field:

```json
{
  "title": "Order entry",
  "steps": [
    { "action": "type", "target": "17", "value": "Morgan" },
    { "action": "toggle", "target": "24", "value": "on" },
    { "action": "click", "target": "30" }
  ]
}
```

The numbers must come from an actual inspection. Steps support `click`, `type`
(replace text, including keyboard-only editors, or set a slider position in its
own range), `keys`, `read`, `select`, `toggle`, `expand`/`collapse`, `scroll`
(`up`/`down`/`left`/`right` by page, or `top`/`bottom`), and condition-based
`wait`. `select` finds combo-box and list options without opening the drop-down,
including rows a virtualized WPF/UWP list has not created yet; click-by-name
realizes such rows too. `click` with `value` `double` or `right` sends a physical
double-click or right-click and needs `allowForeground=true`; an opened context
menu is listed in the observation, and its items can be targeted. `ui_find` also
matches values, so grid cells and rows are found by their contents. A batch validates its arguments before
acting, runs serially within one window and its owned dialogs, stops on the first
failure, and returns per-step timings plus one final observation. Set
`observe=false` when a final read/wait already verifies the outcome. Inspect the
state before retrying a failed step: it may have partially applied. Keyboard and
mouse fallbacks check foreground ownership; bulk text input leaves the clipboard
unchanged. Closed or evicted controls expire rather than having their numbers
reassigned. These tools do not bypass elevated-app permissions or inaccessible
canvas-only UIs.

Windows' UI Automation client blocks for about two seconds on each mutating
pattern call (select, toggle, invoke, expand, set value) when the target cannot
take focus: in a disconnected or locked session, and for Win32 controls whose
built-in proxies try to focus the window. Lumi therefore uses native control
messages for Win32 edits, buttons, combo boxes, list boxes, tabs, trackbars and
scroll bars, and the element's MSAA (LegacyIAccessible) interface for
selection, presses, checkboxes, tree/expander expansion and grid-cell values.
Each fast path sends the owner notifications a user action would, is verified,
and falls back to the UIA pattern. In a disconnected or locked session, background
actions and screenshots keep working, while physical input reports why it cannot
be delivered instead of claiming success.

Actions wait within their step timeout for a known target to become enabled; a
load action can therefore be followed directly by its known Apply button. Explicit
text waits still compare exact values, not guessed message prefixes. WinForms
menu items require foreground permission and verified pointer activation because
their accessibility Invoke callback can otherwise block a modal file dialog.

Native tab controls switch through the control itself, which notifies its owner
like a click; web-backed tabs use their default action. Physical activation
remains an explicit fallback.
Verify a destination-specific heading or primary action, not just the selection
highlight. The snapshot returned by `ui_do` is normally enough for the next
decision; avoid repeated equivalent searches.

#### Window screenshots and the Desktop workspace page

`ui_screenshot(title, maxWidth)` returns an actual PNG image to the model plus a
capture ID, timestamp, and image dimensions. It asks the application to render its
window without bringing it to the foreground; it does not capture other windows
covering it. Minimized windows are restored without activation. Protected or some
GPU-rendered applications may not supply a usable background capture.

For visually exposed controls without useful UIA metadata, use
`ui_click_at(captureId, x, y, allowForeground: true)`. Coordinates are pixels in
the returned image, with the origin at the top-left, not arbitrary screen
coordinates. Clicks reject changed window identity, position, size or DPI,
out-of-image points, blocked windows and covered targets. Captures expire after
two minutes, are superseded by a newer screenshot, and are consumed by a
coordinate click. Take a fresh screenshot after changing the UI. This fallback
uses the real mouse and can interrupt the user; prefer native background actions.

After desktop automation is used, the **Desktop** shortcut opens a read-only page
in the shared **Workspace** panel, with the last screenshot, target window,
action and status. It is not an embedded remote window or a live video stream.
Explicit opening requests a fresh snapshot; further action snapshots update an
already visible Desktop page. Closing it preserves the last frame but subsequent
updates do not reopen it or navigate away from another page. Each host owns and
releases its decoded image; screenshot bytes stay in memory rather than being
added to saved chat data.

#### Validating computer use from the repository

One command builds what it needs and runs every automated layer, reporting each
test and benchmark scenario individually (`summary.json` under the output folder):

```powershell
# Contract tests, real-desktop native tests, and real-model benchmarks (default tiers)
pwsh tools\automation\Invoke-UIAutomationValidation.ps1

# Only the fast, model-free layers, or a model subset repeated three times
pwsh tools\automation\Invoke-UIAutomationValidation.ps1 -Tiers Contracts,Native
pwsh tools\automation\Invoke-UIAutomationValidation.ps1 -Tiers Model -Scenarios tree,grid,wpf -Iterations 3
```

- **Contracts** — tool contracts, prompt guidance, schemas and Desktop preview
  tests; the headless preview UI tests run in their own test host.
- **Native** — `UIAutomationDesktopTests` against disposable WinForms and WPF
  fixtures, including per-step speed limits that catch missed background paths.
  When no Notepad window is open, the command also prepares a disposable file for
  the real-Notepad test and closes that window afterwards if it saved cleanly.
- **Model** — builds Debug Lumi into `.mcp-run`, launches it with an isolated,
  seeded app-data folder (never your real chats, memories or settings; it reuses
  the machine's Copilot sign-in), runs every benchmark scenario with
  `gpt-6-sol`/`low` by default, and then closes only that instance by PID.
- **BattleNet** (opt-in) — tests against a real, signed-in Battle.net; add
  `-BattleNetNavigation` for the navigation benchmark.

Skips and unattempted items never count as passes: the exit code is 0 only when
everything selected passed (2 when something was skipped; `-AllowSkips` accepts
that). Physical-input tests and scenarios need an unlocked, connected, idle
desktop and skip with the reason otherwise; background ones also run while the
session is disconnected or locked, which is itself a supported way to use Lumi.

#### Repeatable Windows desktop benchmarks

Run on an idle interactive desktop against an **isolated Debug Lumi instance**
that is signed into Copilot (the validation command above does this for you).
The suite creates a fresh chat for every case and never sends to an existing
chat. The cases cover:

- An eight-field native order form and a modal preferences dialog.
- A searchable 200-item catalog and an actual scroll-to-end acknowledgment.
- Unicode document editing through a real Windows Save As dialog and menu.
- An asynchronous report that must become ready before its action is enabled.
- Reading a reference and transferring it into a separate owned window.
- Expanding a collapsed, checkable tree to a nested city without checking rows (`tree`).
- Finding an offscreen invoice by value in a data grid and editing a cell (`grid`).
- A modal setup wizard with radio buttons, a spinner, a slider and gated Next (`wizard`).
- Recovering from an error message box by using its suggestion (`recovery`).
- Archiving a file through a right-click context menu and confirmation (`context-menu`).
- A WPF app with a 2,000-row virtualized list, slider, expander and combo box (`wpf`).
- Real Windows Notepad editing and exact saved-file verification (`notepad`).
- Real Windows Calculator arithmetic, verified from its display (`calculator`).
- Screenshot-only identification and a coordinate click on a painted target, and
  double-click/right-click delivery on painted targets (`vision`, `vision-gestures`).

The fixtures use installed Windows
PowerShell/WinForms and independent state/file assertions; the model cannot pass
by merely claiming completion. Visual cases require screenshots before and after
the action plus exact native click counters and the independently generated code.
A visual workflow may recover once with a fresh screenshot and a new capture ID;
all attempts remain in timings and `usedAdditionalAttempt` distinguishes recovery
from one-shot completion. The actual delivered click counts remain exact. Missing
tool output is never treated as proof that an attempted click was rejected.

```powershell
.\tools\automation\Run-UIAutomationBenchmarks.ps1 `
  -LumiProcessId <debug-pid> -OutputDirectory .mcp-run\uia-benchmarks `
  -Label baseline -Model gpt-6-sol -ReasoningEffort low -TimeoutSeconds 240

# Run the same command against the improved Debug PID with -Label improved.
.\tools\automation\Compare-UIAutomationBenchmarks.ps1 `
  -ResultPath <baseline-run-directory>,<improved-run-directory>
```

For repeated native-form measurements, add `-FixtureOnly -Iterations 3` to both
runs, or select a small cohort with `-Scenarios document,delayed,scroll,transfer`.
`-FixtureOnly` excludes the real apps (Notepad and Calculator). Physical-input
cases (`notepad`, `document`, `context-menu`, and the vision cases) need an
unlocked, connected desktop; unavailable input is recorded as a skip, never a pass.

The real apps never touch a window the user opened. Notepad opens the temporary
file in a new window when no Notepad window is visible, or as a tab in a window
left by an earlier benchmark (titled `Lumi-UI-Bench-…`); any other open Notepad
window means the case is skipped. `-NotepadWindowHandle <handle>` still selects
an explicitly prepared test-only window (Notepad's **File > New window**). The
runner never closes Notepad windows or kills its process. Calculator is skipped
when it is already running; otherwise the runner launches it, addresses only that
window by its `hwnd:` selector, and closes that window afterwards.

On a timeout the suite stops scheduling work. If the chat is still active or its
state cannot be confirmed, its native fixture is retained too: stop that test
chat before closing its target. This prevents a still-running model from sending
input after its intended window has been removed.

`results.json` is authoritative: it includes failures/skips, exact prompts and
model settings, independent assertions, fresh-chat end-to-end time, tool-call
counts, and recorded tool durations. End-to-end time includes model/network and
session-startup latency; it is **not** a native-input latency measurement.
Unavailable tool outputs/timings remain null. The comparer refuses speedup claims
for mismatched contracts, incomplete measurements, or cohorts containing failures.
Artifacts remain local and may contain visible window titles. The UI-only prompt
is audited, not a sandbox.
Internal `checkpoint.md` bookkeeping is permitted and counted; it cannot satisfy
any desktop task's independent success assertions.

These are representative workflows, not a guarantee for every desktop app.
The native regression suite also exercises wrong-window/duplicate-title
rejection, disabled and stale controls, partial failures, bounded waits,
minimized recovery, foreground preservation, long Unicode text, and screenshot
invalidation. Unsupported native providers, protected/elevated windows, and
applications that do not render capturable content remain explicit limitations.

Focused native regression tests use the same disposable fixture and are opt-in:

```powershell
$env:LUMI_UI_AUTOMATION_DESKTOP_TESTS = "1"
dotnet test tests\Lumi.Tests\Lumi.Tests.csproj `
  --filter "FullyQualifiedName~UIAutomationDesktopTests"
Remove-Item Env:\LUMI_UI_AUTOMATION_DESKTOP_TESTS
```

For the real Battle.net navigation regression, keep Battle.net open and run:

```powershell
$env:LUMI_BATTLENET_UI_TESTS = "1"
dotnet test tests\Lumi.Tests\Lumi.Tests.csproj `
  --filter "FullyQualifiedName~BattleNetAutomationTests"
Remove-Item Env:\LUMI_BATTLENET_UI_TESTS

.\tools\automation\Run-BattleNetNavigationBenchmark.ps1 `
  -LumiProcessId <debug-pid> -OutputDirectory .mcp-run\battlenet-benchmarks `
  -Label before -Iterations 2
```

Repeat the benchmark against the fixed Debug PID with `-Label after`. Both use
the same `gpt-6-sol` / `low` prompt and start on Diablo IV. They only navigate to
Heroes of the Storm and read its primary action; they never press Install, Play,
Update, Pause, or Uninstall. Actual content is verified independently of the
selected-tab flag. The native test additionally checks that the default
observation includes the destination's deeply nested primary action. The script
loads FlaUI from the built validation output by default; use `-BinaryDirectory`
to select another existing Lumi Debug output directory.

Do not run desktop tests or benchmarks concurrently: Windows has one shared
foreground window and keyboard. Non-Windows builds neither expose nor advertise
the desktop tools.

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
