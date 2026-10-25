# Development reference

[Back to the README](../README.md) | [Contributing](../CONTRIBUTING.md) | [Usage reference](user-guide.md)

Start with [CONTRIBUTING.md](../CONTRIBUTING.md) for tooling, a clean clone,
isolated development profiles and focused validation. This page preserves the
longer build, runtime and diagnostic notes previously in the README.

**Version scope:** these notes describe this source checkout, not necessarily the
latest packaged release. Run repository commands from the repository root unless
a command explicitly changes directory. Set an
[isolated profile](../CONTRIBUTING.md#run-with-an-isolated-profile) before launching
development builds.

Quick links: [build and runtime](#build--run), [embedded browser](#embedded-browser),
[Windows automation](#windows-computer-use-automation), [mobile development](#mobile-development),
[automated QA](testing.md), [critical user journeys](critical-user-journeys.md),
[Strata](#local-stratatheme-development), [architecture](#architecture).

## Getting Started

### Prerequisites

- Git and the [.NET 11 SDK](https://dotnet.microsoft.com/download), currently
  preview. See [required versus optional tooling](../CONTRIBUTING.md#tooling).

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
`1.0.23-preview.5`, restored from NuGet.org. This is a prerelease; its native
skill-provider API is experimental. Avalonia is also restored from NuGet.org;
no vendored package feed is needed. No SDK submodule, source build, patch step,
feed credentials, or Node.js installation
is needed to build Lumi.

Lumi implements the upstream `ISkillProvider` contract for native, lazy in-memory
skill loading. The native `skill` tool handles activation alongside file-based
skills, including reloads, resumed sessions and delegated agents. Lumi's skill
storage, editing and existing app-data Markdown mirrors are unchanged. Native
activation does not depend on those mirrors; no workspace `SKILL.md` stubs or
replacement loader tool are generated.

The selected agent's authored prompt is supplied once by the Copilot runtime,
not pasted into Lumi's system prompt a second time. Lumi preloads each linked
or chat-selected skill once by its ID, including when the same skill is selected
through both paths. Different skills with identical text remain distinct.
This applies to new and resumed sessions; agent tool restrictions and delegated
agents' native skill preloads are unchanged.

Official [Avalonia 12.1.4](https://github.com/AvaloniaUI/Avalonia/releases/tag/12.1.4)
includes both accepted text fixes: batched input-method selection notifications
and empty bounds for zero-length text ranges. Lumi and Strata use the official
packages without a custom core override, local feed mapping or runtime patch.
`Avalonia.Controls.DataGrid` is released independently and remains pinned to its
published `12.1.2` release in `Directory.Build.targets`. The focused input and
text-layout regression tests are retained.
When switching an existing build from the former custom package, use a clean rebuild
(`dotnet build src/Lumi/Lumi.csproj -t:Rebuild`) or a fresh `--artifacts-path`.

**GitHub sign-in:** [Lumi's CLI acquisition target](../build/Copilot/CopilotCli.targets)
supplies one checksum-pinned, matching official full Copilot CLI through the SDK's
supported `CopilotCliBinaryPath` override. The SDK's default headless bundle cannot
replace the full CLI used for sign-in. The official SDK handles copying the binary
to build, referencing-project and publish outputs. The SDK uses it over stdio,
and GitHub's `copilot login` handles browser/device authorization.
The current full CLI does not expose `logout`, so Lumi uses the SDK account API
to remove only the selected stored user. Existing credential selection and
storage are unchanged; AI Models refreshes the shared sign-in display.
No additional CLI, OAuth app, token store, or Node.js installation is required.
The [reviewed CLI pins](../build/Copilot/CopilotCliPins.props) must be updated when an
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

Build from the repository root:

```powershell
dotnet build src\Lumi\Lumi.csproj
```

On macOS/Linux use `src/Lumi/Lumi.csproj`. Before launching, follow the
[isolated-profile run commands](../CONTRIBUTING.md#run-with-an-isolated-profile);
do not run against your normal app data.

Drawing-free UI tests reuse Avalonia's assembly headless session so cached
geometries keep one owning UI thread. Each dispatch still gets a fresh
application and services through `PerTest` isolation; dispose the test wrapper,
not the shared dispatcher. UI objects, including geometry bounds, must be
accessed inside `HeadlessTestSession.Dispatch`.

Nightly or manually requested full Windows CI runs
`tools\Test-LumiTestsInBatches.ps1` after the Release build. Ordinary PRs use
the deterministic CoreQA gate; see [Automated QA](testing.md) for the exact
component/headed split and the release gate.
The Skia-backed `AvaloniaTextInputTests` and `AvaloniaTextLayoutTests` each run
in a fresh test host, separate from the drawing-free UI tests. Mixing these
dispatchers lets delayed callbacks from an earlier window access its controls
on another UI thread and fail an otherwise unrelated test. Keep these classes
isolated rather than retrying or skipping their failures.

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
awaiting with a bounded timeout. Each Windows tab retains its own native window
and rounded clipping region, including after resizing or moving its chat to another window.
Native browser tools keep synchronous JavaScript
and do not expose those Windows-only options.

Coding Lumi includes all seven browser tools by default. Existing built-in Coding
Lumi agents receive a one-time tool upgrade; later permission changes are preserved.

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
For code ownership and maintenance invariants, see [Embedded browser maintenance](../AGENTS.md#embedded-browser-maintenance).

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

### App Icon

Lumi's icon puts Lumi between you and the model: Lumi's soft sparkle, a smaller lilac sparkle for you
floating at its shoulder, and a dot for the model on the other side. One renderer,
`src\Lumi\Services\AppIconArt.cs`, draws it.
Inside the app (the welcome screen, onboarding, the loading window and Settings → About) and in the tray
it wears the accent chosen in Settings → Appearance and changes with it; on Windows, the tray gets an icon
frame drawn for each display scale. The loading window draws the icon's parts apart (`AppIconArt.RenderPart`)
and moves them on the render thread (`Views\StartupMotion.cs`), so the motion keeps playing while the main
window is built; a Debug build started with `--startup-preview` shows only that window and keeps it open
(a click plays it again). On Windows, when Lumi starts on a welcome screen (onboarding's hello, or a new
chat) with animations on, and the user is still watching the loading window (it is still the active window
and not minimized), the main window takes over through a hand-off (`Views\StartupHandOff.cs`,
`Views\MainWindow.Arrival.cs`): it opens DWM-cloaked, so it lays out and draws out of sight; the loading
mark comes to rest, a card fades in over the loading window and grows into the main window's place, the
main window is uncloaked beneath it, and the card clears while the mark glides into the welcome logo. It
plays once per start, so relaunch an isolated Debug build to see it again. Avalonia fades a group by fading
each thing drawn in it, and restores a control's own opacity whenever it redraws it, so the hand-off never
fades overlapping layers together and fades nothing out through a control's composition opacity. Places in
the overlay layer are worked out in the window's own space (`MainWindow.InWindow`), which stays left to right
in a right-to-left window; mapping through the overlay layer itself goes wrong there, since it mirrors itself
back. Installed icons (the taskbar, Start menu, Dock, launchers and home
screens) keep Lumi's own colours. After changing the art, rewrite every installed icon from a Debug build:

```powershell
dotnet run --project src\Lumi\Lumi.csproj -- --write-app-icons
```

It writes `src\Lumi\Assets\lumi-icon.png` and `lumi-icon.ico`, the icon on `installer-splash.png`
(keeping its words), the Android launcher icons and the web app's icons. The output is deterministic,
so running it again changes nothing.

### Windows Installer

Windows releases use the standard Velopack installer with the branded
`src\Lumi\Assets\installer-splash.png` image, configured through `--splashImage`
in the release workflow. Velopack displays progress and launches Lumi when
installation completes. This is presentation-only: installer locking, repair,
command-line options, code signing, and automatic updates are unchanged.

### Linux Release Packages

The auto-updating Linux release is an AppImage. Make it executable before launching:

```bash
chmod +x "Lumi.AppImage"
./Lumi.AppImage
```

Ubuntu 24.04 requires `libfuse2t64` for AppImages (`libfuse2` on older Ubuntu releases).
If FUSE is unavailable, use the release's `linux-x64-portable.tar.gz` archive
instead. Save or rename it as `Lumi-linux-x64-portable.tar.gz` before running:

```bash
mkdir -p lumi-portable
tar -xzf "Lumi-linux-x64-portable.tar.gz" -C lumi-portable
chmod +x lumi-portable/Lumi
./lumi-portable/Lumi
```

[Current download links](../README.md#download) follow the latest release and are
separate from building these packages from source.

### License notices

Source builds include the repository's unchanged [MIT notice](../LICENSE) in
desktop build/publish output as `LICENSE.txt`, in Android packages as
`assets/LICENSE.txt`, and under the published PWA's `wwwroot` directory (the web
SDK may fingerprint the `LICENSE.txt` filename). The desktop notice remains a
separate file even for single-file publishes. The PWA asset manifest includes its
notice with the other web assets, so the release workflow's bundled PWA carries
it too.

Keep copyright and license notices when redistributing Lumi. Dependencies keep
their own licenses and notices; including Lumi's notice does not replace them.

### Localization

Lumi supports English and Hebrew. Desktop UI text lives in
`src/Lumi/Resources/Strings/en.json` and `he.json`; the source generator exposes typed `Loc` properties,
`Loc.Get` for formatted messages, and `{loc:Str Key}` for XAML. Keep both catalogs in sync, including
format arguments. Labels, notifications, validation errors, transcript chrome, search results and
the human-readable parts of exported capability packs use these resources.

The mobile companion uses `src/Lumi.Mobile/Localization/ChatSessionStrings.resx` and its Hebrew
counterpart. Shared Strata controls keep their reusable English/Hebrew wording in their own label
catalog. Localize display text, not wire values, model IDs, file paths, authored prompts or user
content. Built-in capability descriptions have localized display text while their saved defaults,
stable names and user customizations remain intact. Shared capability descriptors keep their canonical
`Description` separate from the UI-only `DisplayDescription`. Model-facing management reports stay
in English; only UI callers opt into `LumiFeatureManager`'s `localizeOutput` formatting. Job schedules
likewise use canonical wording in the system prompt and translated wording in the UI.
`LocalizationCoverageTests` checks desktop
catalog/format parity, XAML keys, Hebrew search and presentation, and embedded-app startup messages.

### Local StrataTheme Development

The current project files reference `StrataTheme` and `StrataSearch` directly from
the checked-out [Strata](https://github.com/adirh3/Strata) submodule at `Strata/`.
A separate sibling clone is **not** automatically selected.

```sh
git submodule update --init --recursive Strata
git -C Strata status --short --branch
```

For a control-library change, work inside the submodule on an appropriate branch
and coordinate the Strata change separately. Lumi records a specific Strata commit;
update that pointer only when the corresponding library change is ready, rather
than silently switching everyone to an unrelated upstream tip.

**Licensing note:** [Strata's pinned README](https://github.com/adirh3/Strata/blob/5e2350606151783611f6b7a2b0866a93464fdc52/README.md#license)
declares MIT. That tree has no standalone license file containing the full notice
(checked October 24, 2026). Lumi's license file covers this repository, not that
separate checkout; confirm the upstream copyright and full license notice when
redistributing Strata. Adding that notice upstream is a maintainer follow-up.

## Mobile development

The shared mobile UI and desktop simulator build with the ordinary desktop
solution. A simulator development run should use its own settings and pairing
directory as well as an isolated desktop profile.

Windows PowerShell:

```powershell
$env:LUMI_APPDATA_DIR = Join-Path ([System.IO.Path]::GetTempPath()) "Lumi-dev-$PID"
$env:LUMI_MOBILE_DATA_DIR = Join-Path ([System.IO.Path]::GetTempPath()) "Lumi-mobile-dev-$PID"
dotnet run --project src\Lumi.Mobile.Desktop\Lumi.Mobile.Desktop.csproj -- --design-preview
```

macOS/Linux:

```sh
export LUMI_APPDATA_DIR="$(mktemp -d "${TMPDIR:-/tmp}/lumi-dev.XXXXXX")"
export LUMI_MOBILE_DATA_DIR="$(mktemp -d "${TMPDIR:-/tmp}/lumi-mobile-dev.XXXXXX")"
dotnet run --project src/Lumi.Mobile.Desktop/Lumi.Mobile.Desktop.csproj -- --design-preview
```

The Debug-only `--design-preview` and `--popup-preview` fixtures use synthetic
data without pairing or model calls. See the
[mobile UI reference](user-guide.md#mobile-conversations) for device and popup
options. Omit the preview flag to test an actual paired connection.

The Android and browser hosts are intentionally outside `Lumi.slnx`:

| Host | Additional tooling |
|---|---|
| Android (`net10.0-android`) | The `android` workload, JDK 17 and Android SDK dependencies. CI uses the .NET 11 preview SDK to build this .NET 10 target. |
| PWA (`net10.0-browser`) | .NET 10 SDK in addition to .NET 11, and `wasm-tools-net10`. Release publishes use WebAssembly AOT; that is not the desktop build mode. |

Install extra workloads only when working on those hosts. The
[PR workflow](../.github/workflows/pr-validation.yml) has the exact current
workload, Java and Android SDK dependency setup. After that setup, focused
Windows PowerShell commands are:

```powershell
dotnet build src\Lumi.Mobile.Android\Lumi.Mobile.Android.csproj -c Debug -f net10.0-android -r android-arm64
dotnet publish src\Lumi.Mobile.Browser\Lumi.Mobile.Browser.csproj --configuration Release --output .mcp-run\pwa-publish
```

On macOS/Linux use `/` in those paths. Android Debug packages use development
signing. Production signing is release-maintainer work; contributors do not need
release keys or a production keystore for Debug development.

For a source-built desktop serving the PWA, set `LUMI_REMOTE_WEB_ROOT` to the
published `wwwroot` directory in the terminal that launches the isolated desktop:

```powershell
$env:LUMI_REMOTE_WEB_ROOT = (Resolve-Path .mcp-run\pwa-publish\wwwroot).Path
```

The shipped desktop packages already include the PWA. Native Android and the PWA
both need the reachable, running desktop; Microsoft Dev Tunnel remains PWA-only.

## Architecture

```
src/Lumi/
├── Models/          — Domain entities (Chat, Project, Skill, Agent, Memory)
├── Services/        — CopilotService, DataStore (JSON + SQLite chats), SystemPromptBuilder
├── ViewModels/      — MVVM ViewModels with CommunityToolkit.Mvvm generators
└── Views/           — Avalonia XAML views + code-behind
```

Settings, projects, skills, agents and memories are saved in `%AppData%/Lumi/data.json`. Chats and their
transcripts live in a local SQLite database next to it, `lumi.db`, so a chat opens with only its newest
exchange in memory and older history is read as you scroll to it (a delegated agent's own steps only when
you open its page); exchanges you scroll away from, and the
older part of a long session once Lumi is idle, are let go again — memory follows what is on screen, not
how long a chat is. Search reads the same database through a text index, so Ctrl+K finds words anywhere
in your messages and Lumi's answers (three letters or more, ignoring case and Latin accents; shorter words
match titles) and Find in chat searches a chat's whole history, without loading either. Deleting chats gives
their space back to the disk. If the database is ever damaged, Lumi moves it aside as
`lumi.damaged-<time>.db`, starts a new one and copies back every chat that still reads; an update that
changes the database's layout applies it once at startup, while the startup window says so. The first start
after updating moves existing chats into the database in the
background (a chat you open first moves right away); the previous files stay as `chats.pre-sqlite-backup`
and `data.pre-sqlite.json`, and are deleted only with the chats they hold (or by Clear All Chats).

The transcript keeps reasoning before the answer it explains, including GPT reasoning summaries that
arrive after the answer. `TranscriptBuilder` normalizes trailing reasoning when a reply settles and when
saved history is rebuilt; reasoning followed by more work stays in order. This changes presentation only,
not the saved messages, their timestamps or the Copilot session.

The local MCP proxy is organized by responsibility:

- `McpProxyRuntime` owns HTTP routing and registration; its `.Registration` partial contains leases.
- `McpStdioServerConnection` owns backend lifecycle; `.Discovery`, `.Process`, and `.Transport` keep catalog validation, process management, and message forwarding separate.
- `McpDiscoveryCache` stores snapshots, while `McpDiscoverySession` holds each client's advertised contract.
- `JsonRpc` contains the shared message-format helpers.
