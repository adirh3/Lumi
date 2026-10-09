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
- **CSV previews** — Preview CSV and TSV files as clean, themed, read-only tables with sortable/resizable columns, row search, footer stats, full-value tooltips, and row copying
- **Localization** — English and Hebrew, with easy extension to other languages
- **Desktop notifications** — Toast notifications when responses complete in the background
- **Visible startup** — A localized loading window appears before the main UI is built; minimized launches stay silent, onboarding screens are created only when needed, and Copilot connects after the first frame

### CSV file previews

Choose **Preview** on a CSV attachment or file in the Workspace/Library to open a
spreadsheet-style table. Column headers stay visible while scrolling; row numbers
stay pinned and keep their original record positions when filtering or sorting.
Click a header to sort (numeric columns sort numerically), drag its edge to resize,
and search across every cell. Row/column counts and separator information sit beneath
the table. For files without column names, right-click the table and choose **Use
first row as data**; the same menu switches back to using the first row as headers.
Hover a cell to see its full value; select rows and press **Ctrl+C** (**Cmd+C** on
macOS) to copy them with headers.

Comma, semicolon, tab, and pipe separators are detected automatically, including
Excel's `sep=` directive. Quoted separators, escaped quotes, multiline fields,
empty cells, and Unicode BOMs are supported. TSV files use tabs. Like other text
previews, the viewer reads at most 100,000 characters; large files show a notice
and only complete records, never a misleading partial row. **Refresh** reloads
the file, and **Open** still opens it in its default app. Previewing never edits
the source file.

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

### Pause and resume chats

While a chat is working, **Pause** appears above the composer beside its activity
and context controls, and in its sidebar context menu. Idle chats have no pause
control, and the title bar stays clear. A paused chat gets a pause marker and a
**Resume** panel above the composer; your draft stays editable.
Messages submitted while paused are visibly queued until the chat resumes.
That queue survives app restarts, retaining message order, attachments, and replies.
The **… Chat actions** menu beside **New Chat** offers **Pause all chats** when
chats are active and **Resume all chats** when chats are paused, across projects
and detached windows. These are menu actions, not a standing sidebar toolbar.
New chats are not automatically paused.

Pause interrupts the SDK's current turn, cancels its attached shell/agent tasks,
and confirms they are no longer running before showing **Chat paused**.
Interrupted tool cards settle as **Stopped**; completed tool cards remain
completed. Tool-completion callbacks are never held open by pause.

Resume uses the SDK's `session.sendMessages` with an **empty message batch**.
It runs over the existing session history without adding a user message or
sending a synthetic "continue" prompt. This resumes the task from preserved
conversation state, not a frozen process instruction pointer; interrupted
commands may be retried by the agent. Pause during initial setup holds submission
instead, so the original prompt is still sent only once on resume.

Pause intent and pending continuation are saved with chat metadata. Resume after
a restart reattaches to that original SDK session and continues its history.
If the original session cannot be restored, Lumi reports the failure instead of
silently creating a replacement. Sends from another chat or device are rejected
with a clear paused-chat message until resumed.

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

Lumi's native Android app is distributed as an official APK on
[GitHub Releases](https://github.com/adirh3/Lumi/releases/latest), not through Google Play
or any other app store. In **Settings > Mobile**, choose **Android app** and scan the
setup code to download the APK matching your desktop release. Install it, return to
the setup page, and tap **Open Lumi**. If the app is not installed, **Open Lumi**
falls back to the official APK download, never an app-store listing.

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
2. If Microsoft sign-in is missing or expired, choose **Sign in with Microsoft**.
   Lumi runs the CLI's normal interactive login. On Windows, this uses Microsoft's
   account broker, as `devtunnel login` does in a terminal. The sign-in process
   retains a hidden console context so the CLI can locate Lumi's visible parent
   window; management and hosting commands remain windowless. No terminal is shown.
   Complete sign-in with the account you want to use on the phone.
   Personal Microsoft and Microsoft Entra accounts work; GitHub authentication
   is not accepted for this connection. Lumi does not wait for silent Windows
   authentication or launch an interactive login during unattended startup.
   If the desktop sign-in cannot open, expand **Having trouble signing in?** and
   choose **Use browser instead**. This explicitly requests a device-code flow;
   the trusted Microsoft address and code are then shown with Open/Copy actions.
   The CLI can also fall back to this flow itself if its native broker is unavailable.
   Device codes are not forced as the primary method, which also preserves support
   for organizations that block device-code authentication.
   Sign-in is requested only when needed, not offered as an unnecessary re-login
   on a working connection. Microsoft's CLI replaces its cached login when a new
   sign-in starts; canceling that operation leaves sign-in pending, while the saved
   link and Lumi device pairing remain intact.
   The visible sign-in flow allows up to 15 minutes for account selection and MFA,
   rather than canceling a still-valid Microsoft code after five minutes.
   **Cancel setup** cancels the owned operation without closing your browser.
3. Setup continues automatically and displays the verified account, HTTPS link,
   and QR code. The Web app setup opens automatically when you select Dev Tunnel.
   Lumi generates its separate pairing code only once the link is ready.
4. Open that link on your phone, sign in with **the same account**, and enter the
   separate, single-use Lumi pairing code displayed on the PC.

Only the current setup step is shown. Browser fallbacks and error details stay
collapsed until needed. Once connected, the setup card is replaced by the phone
QR code and pairing code; the redundant Web/Android chooser is hidden for this
web-only transport. This handoff is automatic on startup and when phone access
is re-enabled, not only when the connection method is first selected.
The open Mobile settings page brings the QR and pairing-code panel into view
when it is ready; background setup does not navigate away from another page.
If the web assets are missing, setup shows that error rather than hiding the
panel behind a connected relay state. Installation and connection tips are
available on demand.

Downloads use fixed HTTPS URLs from Microsoft's official Dev Tunnel distribution
account, with redirects disabled, size/time limits, and an executable-format check.
On Windows, a valid Microsoft Authenticode signature is also required before the
download is installed or executed. The verifier uses Windows PowerShell's built-in
security module, not inherited PowerShell Core or user module paths.
Other platforms authenticate the distribution
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

Lumi creates or reuses only its own profile-specific tunnel and verifies that
**both the tunnel and its HTTP port have no additional access grants before
hosting**. Unexpected ports, protocols, permissions, account changes, or cluster
relocation stop setup rather than silently changing the access policy.
It never enables anonymous,
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

Published PWAs cache their static app shell and WebAssembly runtime locally after
the first successful load. Later icon launches can open the app without waiting
for those assets to cross the tunnel again. Chats, pairing responses, event
streams, commands, and downloaded files are **not** service-worker cached; the PC,
tunnel sign-in, and device pairing are still required for live use. Plain HTTP
LAN addresses do not support service workers; HTTPS and localhost do.

Private Dev Tunnel icon launches check the opening document over the network,
even when its shell is cached. This lets Microsoft's gateway renew its browser
session before the live API starts; authentication redirects and denials are
never replaced with cached application HTML. The WASM/static assets still open
from their verified cache. Offline or unavailable-tunnel launches retain the
cached shell, with live data remaining unavailable until the connection returns.
Other supported origins keep their cache-first navigation behavior.

The web API transport explicitly includes same-origin browser credentials, uses
Fetch `cache: no-store`, and keeps redirects manual. This applies to every live
request, including reconnects and event subscriptions, so browser HTTP caches
cannot reuse an earlier gateway denial after sign-in is renewed.

The manifest requests **focus-existing** launch behavior, so browsers that
support it bring an open Lumi window forward without navigating it again. This
is a browser-dependent hint, not a guarantee on Android: Edge/Android may still
reload or recreate the page, and Android can discard background apps. Resuming
an existing page no longer performs duplicate lifecycle handshakes. Browser
theme/status-bar colors follow Lumi's Light, Dark, or System preference.

The PWA checks for content-versioned updates on launch, resume, network recovery,
and a live desktop reconnect. New builds download and integrity-check their
static assets before activation. When there is no unsent work or open editor,
Lumi activates the update and reopens once automatically. Otherwise an **Update
ready** banner keeps the current app usable and defers activation/reopening until
drafts (including other chats and question replies), attachments, uploads,
produced-file downloads and opens, pending sends/configuration or remote actions,
and open editors or sheets are safe. Update failures offer **Try again** without
clearing the working app or pairing. Restarting the desktop with unchanged PWA
assets only reconnects; it does not reinstall the bundle or reload the app.
Other open app windows keep their runtime and drafts until they can safely
reopen too. The refresh URL is a recovery fallback, not the normal update flow.

If tunnel sign-in expires, the PWA stops treating it as a network reconnect and tries one
same-origin sign-in refresh only when pending actions have finished and no unsent
or edited work would be lost. That navigation bypasses the static cache so Microsoft's gateway can renew its session;
only a confirmed live Lumi connection resets the automatic-attempt guard.
Otherwise, **Sign in again** appears in the connection banner. The existing
**Settings > Connection > Browser sign-in > Reload web app** action remains
available. Pairing stays saved, but copy any unsent work before a manual reload.
Startup failures also offer **Try again**.

The explicit reload goes through a small, non-cached recovery document. It
unregisters only Lumi's `/app/` service-worker registration before opening a
fresh document, so a browser retaining an older controller does not reload the
same stale runtime. It does not clear cookies, pairing, browser storage, or
verified static caches, and it does not navigate other open app windows.
The recovered document coordinates activation of the waiting update. Older
verified framework caches are retained while other app pages remain open, so
those pages keep their loaded runtime and drafts; subsequent new launches use
the current build instead of depending on the browser retiring an old page.

**Install as app** does not guarantee a separate Android application identity:
the browser decides whether it creates a packaged PWA or a browser-hosted
shortcut, and it controls the Recents icon. Authentication-protected manifests
and icons may limit packaging by services that cannot use the browser's sign-in
cookies. Lumi keeps those assets private; it does not expose the tunnel publicly
to force an install icon. Reinstall from the current signed-in `/app/` page after
updating, but do not treat that as a guaranteed fix for Edge's Recents icon.

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
access tokens. Keep the PC awake and Lumi running; the phone cannot reach a sleeping
or powered-off PC.

The tunnel ID, Microsoft service cluster, and actual listener port are saved, so
reconnecting and restarting keep the same link and browser pairing. A saved port
conflict is shown explicitly instead of silently publishing a different URL.
Lumi enables the access link only after saving the route and port. A local save
failure asks you to check disk space and folder access; it is not treated as a
relay outage, and a manual retry saves again before hosting.
Turning off phone access or switching transport stops the owned relay immediately
but retains the private tunnel resource for reuse. Its idle expiration is configured
to 30 days. Microsoft automatically extends this inactivity window with activity;
Lumi does not run a separate renewal timer. If the PC remains inactive long enough
for Microsoft to expire the resource, Lumi recreates the saved route in its original
cluster when available.

Transient relay failures are retried automatically with capped, cancellable
backoff, and network changes rehost the same route. Settings show **Reconnecting**
instead of a usable link while the relay is down; **Reconnect** retries without
rebinding the local listener. Lumi observes the CLI's live relay-close, restored,
and terminal host-error messages instead of waiting for the process to exit.
The CLI can recover in place; if its recovery stalls for 90 seconds or reports a
recoverable terminal host error, Lumi replaces only its owned host through the same retry
path. A previously connected host can hold expired in-memory credentials, so
Lumi first relaunches it to reacquire credentials from Microsoft's credential
store. If fresh setup still requires authentication, Lumi asks for explicit
Microsoft sign-in rather than repeatedly restarting or opening login unattended.
The listener, saved link, phone pairing, and running chats remain unchanged.
An explicit "another host has connected" conflict instead stops automatic
recovery and shows an error. Stop the other host before manually retrying; Lumi
does not automatically reclaim the tunnel from it.
On the phone, Microsoft's gateway session can expire separately
from Lumi pairing: reopen the same `/app/` link and sign in again with the owner
account. This does not revoke or replace the phone's Lumi pairing token.
The web client identifies gateway sign-in redirects and non-Lumi authentication
errors separately from Lumi's marked JSON pairing errors, so an expired Microsoft browser
session does not accidentally unpair the device. Authentication failures stop the
handshake and event-stream retry loops and expose **Sign-in required** with a
direct recovery action, rather than endlessly showing **Reconnecting**.
Copy any unsent draft before reloading the web app.
Long-lived web requests use the CLI's disabled relay request timeout; Lumi's own
request/body limits and SSE silence deadlines remain in force.

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

The Copilot SDK is an official NuGet dependency: `GitHub.Copilot.SDK`
`1.0.17-preview.7`, restored from NuGet.org. This is a prerelease; its native
skill-provider API is experimental. Only the patched Avalonia core comes from
the checked-in `vendor/nuget` feed. No SDK
submodule, source build, patch step, feed credentials, or Node.js installation
is needed to build Lumi.

Lumi implements the upstream `ISkillProvider` contract for native, lazy in-memory
skill loading. The native `skill` tool handles activation alongside file-based
skills, including reloads, resumed sessions and delegated agents. Lumi's skill
storage, editing and existing app-data Markdown mirrors are unchanged. Native
activation does not depend on those mirrors; no workspace `SKILL.md` stubs or
replacement loader tool are generated.

`Directory.Build.targets` pins the core `Avalonia` package to the vendored
`12.1.3.1` build throughout the project-reference graph, including Strata. It fixes
input-method notifications exposing partially updated selections and empty
selection geometry reaching the text-line renderer. Official Avalonia peer
packages remain at `12.1.3`, except `Avalonia.Controls.DataGrid`, which is pinned
separately to its published `12.1.2` release. No Strata input guard or runtime
patch is used.
See [Avalonia patch provenance and reproduction](vendor/avalonia/12.1.3.1/README.md).
When switching an existing build to the patched package, use a clean rebuild
(`dotnet build src/Lumi/Lumi.csproj -t:Rebuild`) or a fresh `--artifacts-path`.

**GitHub sign-in:** [Lumi's CLI acquisition target](build/Copilot/CopilotCli.targets)
supplies one checksum-pinned, matching official full Copilot CLI through the SDK's
supported `CopilotCliBinaryPath` override. The SDK's default headless bundle cannot
replace the full CLI used for sign-in. The official SDK handles copying the binary
to build, referencing-project and publish outputs. The SDK uses it over stdio,
and GitHub's `copilot login` handles browser/device authorization.
The current full CLI does not expose `logout`, so Lumi uses the SDK account API
to remove only the selected stored user. Existing credential selection and
storage are unchanged; AI Models refreshes the shared sign-in display.
No additional CLI, OAuth app, token store, or Node.js installation is required.
The [reviewed CLI pins](build/Copilot/CopilotCliPins.props) must be updated when an
SDK upgrade changes `CopilotCliVersion`; both downloaded archives and extracted
executables are verified. `CopilotSkipCliDownload` and `CopilotCliBinaryPath`
overrides remain available.

**Updating Copilot:** choose a specific SDK version and update its `PackageReference`
in `src/Lumi/Lumi.csproj`, then run the maintainer-only pin updater with Python 3.10+:

```powershell
python tools\update_copilot_cli_pins.py
python tools\update_copilot_cli_pins.py --check
```

The script reads the matching CLI version from the official SDK package, verifies
all eight platform archives against GitHub's release checksums, and replaces the
pin file only after every executable passes its layout and hash checks. It reuses
archives in the normal build cache when available; missing archives are downloaded
temporarily. Review and commit the SDK reference and generated pins together.
No SDK is repacked and no CLI binaries are committed. Python is needed only for
this maintenance step, not for normal restore, build or publish. Focused updater
tests run with `python -m unittest discover -s tools -p test_update_copilot_cli_pins.py`.

```bash
dotnet build src/Lumi/Lumi.csproj
cd src/Lumi && dotnet run
```

Drawing-free UI tests reuse Avalonia's assembly headless session so cached
geometries keep one owning UI thread. Each dispatch still gets a fresh
application and services through `PerTest` isolation; dispose the test wrapper,
not the shared dispatcher. UI objects, including geometry bounds, must be
accessed inside `HeadlessTestSession.Dispatch`.

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

### Embedded browser

Lumi's Workspace browser uses the existing WebView2 implementation on Windows
and the system WebKit browser on Linux/macOS. Native support is compiled out on
Windows; the engines keep independent initialization, tab, and disposal code.
The same browser tools provide navigation, safe element targeting, forms, JavaScript,
uploads, tabs, and actual page screenshots on all three platforms.
Windows also provides browser phase-timing diagnostics and JavaScript promise
awaiting with a bounded timeout. Native browser tools keep synchronous JavaScript
and do not expose those Windows-only options.

Linux needs WebKitGTK 4.1 (`sudo apt install libwebkit2gtk-4.1-0` on Ubuntu).
The native macOS browser requires macOS 14 or later for private per-profile
storage; older macOS versions use the system browser instead.
WebKitGTK stores browser data under Lumi's app-data directory; macOS selects a
separate system-managed WKWebView data store for each Lumi app-data profile.
Cookie import uses the user's local browser profiles; unavailable keyring access and unsupported
encryption formats are reported rather than treated as a successful import.
Explicit native cookie import/reset reaches initialized tabs in the same Lumi
profile, without touching other profiles or initializing unopened tabs. Session
cookies are not continuously synchronized into future tabs.

Platform differences are intentional:

- Linux tabs have separate in-memory session cookies. Keep a signed-in workflow
  in the same tab; a new tab or popup may need its own sign-in.
- Native popups open as independent tabs, without a preserved JavaScript
  `window.opener` relationship.
- Embedded downloads are supported on Windows only. On Linux/macOS, use a
  verified direct download URL with `curl`, or the system browser. Native tool
  descriptions omit the download action, and direct requests report the limitation.

For headed regression checks, run the Debug-only fixture:

```powershell
dotnet run --project src\Lumi\Lumi.csproj -- --test-browser-native
```

It creates a fresh isolated app-data directory and a loopback-only fixture server;
it never sends Copilot messages, uses real browser cookies, registers global
shortcuts, or changes launch-at-login settings. Checks exercise the
real Workspace, navigation and actions (including waits during slow navigation),
uploads, native cookie round-trips, Settings import/reset across existing tabs,
profile isolation, tab policy, page-reported viewport
dimensions, screenshot pixels, hide/show, resizing, UI scaling, detached-window
transfer, and disposal. It exits nonzero on failure. Add
`--browser-native-output <directory>` to collect JSON/PNG evidence, or
`--browser-native-keep-open` to leave the fixture open for inspection.
The dedicated validation workflow runs on Windows, Linux, Intel macOS, and Apple
Silicon macOS; headed results, not compilation alone, establish runtime support.
The report's `operationTimings` measures local fixture tool-completion durations;
compare the same host/configuration, not an input-to-pixel or universal website SLA.
For code ownership and maintenance invariants, see [Embedded browser maintenance](AGENTS.md#embedded-browser-maintenance).

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
