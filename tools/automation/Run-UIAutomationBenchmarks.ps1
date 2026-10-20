#requires -Version 7.2
<#
.SYNOPSIS
Benchmarks real Windows computer use through fresh, persisted Debug Lumi chats.
.DESCRIPTION
Run in pwsh on an otherwise idle interactive desktop. Does not build, launch,
reconfigure, or stop Lumi, and never sends to an existing chat. Requires an
already running Debug Lumi PID. Every attempted case gets a new native target
and chat, with the same task values, prompt template, model, effort, and timeout.

The only allowed model computer tools are ui_* (including UI-only batches).
Vision scenarios are narrower: only ui_screenshot and ui_click_at, without wrappers.
They permit one additional coordinate attempt (at most two for vision, three
for vision-gestures), each after a matching fresh capture with a unique captureId.
Native action counts remain exact, and a final screenshot is required. All
attempts stay in counts/timings; missing outputs never imply a rejection reason.
Internal checkpoint.md bookkeeping is allowed and included in tool-call timings.
This is an audited instruction, NOT a sandbox. Do not run unattended on a
desktop containing sensitive work. Only inspect local artifacts; tool output
can contain window titles from ui_list_windows.

All disposable fixtures use system Windows PowerShell 5.1 -STA.
Notepad is the real Windows app: ANY pre-existing Notepad process causes a
safe skip. It is NEVER killed or closed, even when its launcher exits or its
window cannot be proven owned. Close only benchmark-owned Notepad windows
manually between runs; repeated Notepad iterations otherwise safely skip.
FixtureOnly excludes Notepad; its default selection is the eight disposable
fixture cases, including vision. The additional vision-gestures case is opt-in
via Scenarios and is also retained by FixtureOnly. This is NOT a headless SDK
substitute. Both vision scenarios require an
unlocked interactive desktop for physical input and records a precondition
skip otherwise. Skips never count as passes. Use Scenarios to select a subset.
Document saves through a real Save As dialog into its fresh case directory.
Scroll uses a native ListBox and verifies TopIndex/last-row geometry, not
selection or inferred keyboard input; provider support must be tested live.

Elapsed time is send_message through wait_for_idle; setup, persistence, audit,
and verification are excluded. Tool time is the sum of recorded tool durations
(overlapping calls can exceed wall time). Missing timings are null, not zero.
The bridge currently caps text and omits durations; read_activity/read_transcript
are paged, and ONLY this run's new per-chat file is read for complete raw output
and ToolDurationMs. Its path and fixture state are never sent to the model.
Unavailable tool output is recorded as null with per-tool availability; it is
not counted as an empty string. Complete output-character totals require every
tool output to be available.

Artifacts are local JSON under a unique label/timestamp directory. results.json
is authoritative; skipped, failed, and unattempted cases stay in its denominator.
On a timeout or unknown/unsafe chat state the suite stops scheduling cases,
preserves the chat for parent inspection, and retains its fixture if the session
is not confirmed idle. Stop that chat before closing the retained fixture. It
cannot cancel the timed-out model through the current bridge.
.EXAMPLE
.\Run-UIAutomationBenchmarks.ps1 -LumiProcessId 12345 -OutputDirectory C:\Temp\LumiUiBench -Label baseline
.EXAMPLE
.\Run-UIAutomationBenchmarks.ps1 -LumiProcessId 23456 -OutputDirectory C:\Temp\LumiUiBench -Label improved -Iterations 3 -FixtureOnly
.EXAMPLE
.\Run-UIAutomationBenchmarks.ps1 -LumiProcessId 23456 -OutputDirectory C:\Temp\LumiUiBench -Label notepad-retest -Scenarios notepad
.EXAMPLE
.\Run-UIAutomationBenchmarks.ps1 -LumiProcessId 23456 -OutputDirectory C:\Temp\LumiUiBench -Label gestures -Scenarios vision-gestures
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1, 2147483647)][int]$LumiProcessId,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]{0,79}$')][string]$Label,
    [ValidateRange(1, 100)][int]$Iterations = 1,
    [ValidateSet('order', 'preferences', 'catalog', 'notepad', 'document', 'delayed', 'scroll', 'transfer', 'vision', 'vision-gestures',
        'tree', 'grid', 'wizard', 'recovery', 'context-menu', 'wpf', 'calculator')]
    [string[]]$Scenarios = @('order', 'preferences', 'catalog', 'notepad', 'document', 'delayed', 'scroll', 'transfer', 'vision',
        'vision-gestures', 'tree', 'grid', 'wizard', 'recovery', 'context-menu', 'wpf', 'calculator'),
    [ValidateRange(15, 1800)][int]$TimeoutSeconds = 180,
    [ValidateNotNullOrEmpty()][string]$Model = 'gpt-6-sol',
    [ValidateNotNullOrEmpty()][string]$ReasoningEffort = 'low',
    [ValidateRange(0, 9223372036854775807)][long]$NotepadWindowHandle = 0,
    [switch]$FixtureOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
if (-not $IsWindows) { throw 'An interactive Windows desktop and pwsh are required.' }
# Real installed apps are excluded from fixture-only runs; they never run against a user's open window.
$realApps = @('notepad', 'calculator')
# These scenarios need a connected, unlocked desktop: physical keyboard/mouse input, or (Calculator)
# UWP app activation, which a disconnected or locked session does not provide.
$interactiveScenarios = @('notepad', 'document', 'vision', 'vision-gestures', 'context-menu', 'calculator')
$stateKeys = @{ recovery = 'account'; 'context-menu' = 'files' }
$Scenarios = @($Scenarios | Select-Object -Unique | Where-Object { -not $FixtureOnly -or $_ -notin $realApps })
if ($Scenarios.Count -eq 0) { throw 'No scenarios selected.' }

$script:bridgeToken = ''
$script:http = $null
$script:bridge = $null
$fixtureScript = Join-Path $PSScriptRoot 'Start-UIAutomationFixture.ps1'
$visionFixtureScript = Join-Path $PSScriptRoot 'Start-UIAutomationVisionFixture.ps1'
$wpfFixtureScript = Join-Path $PSScriptRoot 'Start-UIAutomationWpfFixture.ps1'
$windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$fixtureHash = (Get-FileHash -LiteralPath $fixtureScript -Algorithm SHA256).Hash
$visionFixtureHash = (Get-FileHash -LiteralPath $visionFixtureScript -Algorithm SHA256).Hash
$wpfFixtureHash = (Get-FileHash -LiteralPath $wpfFixtureScript -Algorithm SHA256).Hash
$runnerHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
$suiteId = [guid]::NewGuid().ToString('N')
$runRoot = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) (
    '{0}-{1}-{2}' -f $Label, [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'), $suiteId.Substring(0, 8))
[void][IO.Directory]::CreateDirectory($runRoot)

function Protect-Text {
    param([string]$Text)
    if ($script:bridgeToken) { return $Text.Replace($script:bridgeToken, '[REDACTED]') }
    return $Text
}

function Write-ArtifactJson {
    param([string]$Path, $Value)
    $json = ConvertTo-Json -InputObject $Value -Depth 100
    [IO.File]::WriteAllText($Path, (Protect-Text $json), [Text.UTF8Encoding]::new($false))
}

function Get-TextHash {
    param([string]$Text)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))) }
    finally { $hash.Dispose() }
}

function Find-DebugBridge {
    $process = [Diagnostics.Process]::GetProcessById($LumiProcessId)
    try {
        if ($process.HasExited) { throw 'The requested Lumi PID has exited.' }
        $processStartedAt = $process.StartTime.ToUniversalTime()
    }
    finally { $process.Dispose() }
    $localRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Lumi'
    $paths = @()
    $directory = Join-Path $localRoot 'debug-bridges'
    if (Test-Path -LiteralPath $directory) {
        $paths += @(Get-ChildItem -LiteralPath $directory -Filter '*.json' -File | Select-Object -ExpandProperty FullName)
    }
    $paths += Join-Path $localRoot 'debug-bridge.json'
    $matches = [Collections.Generic.List[object]]::new()
    foreach ($path in $paths) {
        try {
            $candidate = [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable
            if ([int]$candidate['processId'] -ne $LumiProcessId) { continue }
            if (-not $candidate['startedAt'] -or
                ([DateTimeOffset]$candidate['startedAt']).UtcDateTime -lt $processStartedAt.AddSeconds(-2)) { continue }
            $uri = [uri]$candidate['url']
            if ($uri.Scheme -ne 'http' -or $uri.Host -ne '127.0.0.1' -or
                $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment -or $uri.UserInfo) { continue }
            if (-not $candidate['token'] -or -not $candidate['instanceId'] -or
                -not [IO.Path]::IsPathFullyQualified([string]$candidate['appDataDir'])) { continue }
            $matches.Add($candidate)
        }
        catch { continue } # Discovery files can disappear during another instance's shutdown.
    }
    if ($matches.Count -gt 0) {
        return $matches | Sort-Object { [DateTimeOffset]$_['startedAt'] } -Descending | Select-Object -First 1
    }
    throw "No valid loopback Debug bridge discovery matches PID $LumiProcessId."
}

function Invoke-Bridge {
    param([string]$Action, [hashtable]$Arguments = @{}, [int]$TimeoutMs = 15000)
    $request = [Net.Http.HttpRequestMessage]::new(
        [Net.Http.HttpMethod]::Post, ([string]$script:bridge['url']).TrimEnd('/') + '/invoke')
    $cancellation = [Threading.CancellationTokenSource]::new([Math]::Max(1, $TimeoutMs))
    $response = $null
    try {
        [void]$request.Headers.TryAddWithoutValidation('X-Lumi-Debug-Token', $script:bridgeToken)
        $payload = ConvertTo-Json -InputObject @{ action = $Action; arguments = $Arguments } -Depth 30 -Compress
        $request.Content = [Net.Http.StringContent]::new($payload, [Text.Encoding]::UTF8, 'application/json')
        $response = $script:http.SendAsync($request, $cancellation.Token).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json -AsHashtable -Depth 100
        if (-not $response.IsSuccessStatusCode -or $body['ok'] -ne $true) {
            throw "HTTP $([int]$response.StatusCode): $($body['error'])"
        }
        if (-not $body.Contains('result')) { throw 'Response has no result.' }
        return $body['result']
    }
    catch {
        if ($cancellation.IsCancellationRequested -or $_.Exception.Message -match 'TimeoutException|did not become idle') {
            throw [TimeoutException]::new("Bridge action '$Action' exceeded its deadline.")
        }
        throw (Protect-Text "Bridge action '$Action' failed: $($_.Exception.Message)")
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        $request.Dispose()
        $cancellation.Dispose()
    }
}

function Assert-LumiIdle {
    $status = Invoke-Bridge 'status'
    if ([int]$status['bridge']['processId'] -ne $LumiProcessId -or
        $status['bridge']['instanceId'] -ne $script:bridge['instanceId']) {
        throw 'Bridge identity changed; refusing to use another Lumi instance.'
    }
    if ($status['app']['isBusy'] -or $status['app']['isStreaming'] -or
        $status['app']['isSessionActive'] -or $status['app']['hasBackgroundActivity']) {
        throw 'Lumi has active work. Leave all existing chats untouched and retry when idle.'
    }
    $running = Invoke-Bridge 'list_chats' @{ isRunning = $true; includeEmpty = $true; limit = 1 }
    if ([int]$running['totalMatched'] -gt 0) { throw 'Another chat is running; the desktop is not exclusive.' }
}

# Reading top-level window metadata does not focus or interact with any window.
if (-not ('LumiAutomationBench.NativeWindows' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace LumiAutomationBench {
    public sealed class WindowInfo {
        public long Handle { get; set; }
        public int ProcessId { get; set; }
        public string Title { get; set; }
    }
    public static class NativeWindows {
        private delegate bool EnumWindowProc(IntPtr window, IntPtr argument);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr argument);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint fromThread, uint toThread, bool attach);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder text, int size, out uint needed);
        public static bool HasInteractiveInputDesktop() {
            var desktop = OpenInputDesktop(0, false, 0x0001);
            if (desktop == IntPtr.Zero) return false;
            try {
                var name = new StringBuilder(256);
                uint needed;
                return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out needed) &&
                    string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
            }
            finally { CloseDesktop(desktop); }
        }
        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQuerySessionInformation(IntPtr server, uint session, int infoClass, out IntPtr buffer, out uint bytes);
        [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
        // Disconnected remote sessions keep a Default desktop but cannot receive keyboard/mouse input.
        public static bool HasPhysicalInput() {
            IntPtr buffer; uint bytes;
            if (WTSQuerySessionInformation(IntPtr.Zero, uint.MaxValue, 8, out buffer, out bytes)) {
                try { if (Marshal.ReadInt32(buffer) != 0) return false; }
                finally { WTSFreeMemory(buffer); }
            }
            return HasInteractiveInputDesktop() && GetForegroundWindow() != IntPtr.Zero;
        }
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)]
        private struct Message {
            public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam;
            public uint Time; public int X; public int Y; public uint Private;
        }
        [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
        public static bool Activate(long handle) {
            var window = new IntPtr(handle);
            if (GetForegroundWindow() == window) return true;
            SetForegroundWindow(window);
            if (GetForegroundWindow() == window) return true;
            Message message;
            PeekMessage(out message, IntPtr.Zero, 0, 0, 0);
            uint ignored;
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out ignored);
            var targetThread = GetWindowThreadProcessId(window, out ignored);
            var currentThread = GetCurrentThreadId();
            var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread &&
                AttachThreadInput(currentThread, foregroundThread, true);
            var attachedTarget = targetThread != currentThread && targetThread != foregroundThread &&
                AttachThreadInput(currentThread, targetThread, true);
            try {
                BringWindowToTop(window); ShowWindow(window, 5); SetForegroundWindow(window);
                SetActiveWindow(window); SetFocus(window);
            }
            finally {
                if (attachedTarget) AttachThreadInput(currentThread, targetThread, false);
                if (attachedForeground) AttachThreadInput(currentThread, foregroundThread, false);
            }
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (GetForegroundWindow() != window && wait.ElapsedMilliseconds < 250)
                System.Threading.Thread.Sleep(5);
            return GetForegroundWindow() == window;
        }
        public static WindowInfo[] Snapshot() {
            var windows = new List<WindowInfo>();
            EnumWindows((window, argument) => {
                if (!IsWindowVisible(window)) return true;
                var text = new StringBuilder(2048);
                if (GetWindowText(window, text, text.Capacity) == 0) return true;
                uint processId;
                GetWindowThreadProcessId(window, out processId);
                windows.Add(new WindowInfo { Handle = window.ToInt64(), ProcessId = (int)processId, Title = text.ToString() });
                return true;
            }, IntPtr.Zero);
            return windows.ToArray();
        }
    }
}
'@
}

function Get-NotepadWindows {
    @([LumiAutomationBench.NativeWindows]::Snapshot() | Where-Object {
        $process = $null
        try {
            $process = [Diagnostics.Process]::GetProcessById($_.ProcessId)
            $process.ProcessName -ieq 'notepad'
        }
        catch { $false } # The process exited while enumerating.
        finally { if ($null -ne $process) { $process.Dispose() } }
    })
}

function Start-OwnedNotepad {
    param([string]$Directory, [string]$Token)
    # Only visible windows matter: a new file opens in a new window unless Notepad already shows one.
    $before = Get-NotepadWindows
    $metadata = [ordered]@{
        ready = $false; reason = $null; preExistingWindowCount = $before.Count; hostMode = 'new-window'
        launchProcessId = $null; windowProcessId = $null; windowHandle = $null
        initialWindowTitles = @(); windowTitles = @(); candidateWindows = @()
        retained = $true; filePath = (Join-Path $Directory ($Token + '.txt'))
    }
    $hostHandle = $NotepadWindowHandle
    if ($hostHandle -eq 0 -and $before.Count -gt 0) {
        # A benchmark-owned leftover window can host the new tab; any user window means skip.
        $owned = @($before | Where-Object { $_.Title -match 'Lumi-UI(-Bench|A-Native)-[0-9a-f]{32}' })
        if ($owned.Count -ne $before.Count) {
            $metadata.reason = 'Skipped: a user Notepad window is open, and a new file could open as a tab in it. Nothing was touched.'
            return $metadata
        }
        $hostHandle = $owned[-1].Handle
    }
    if ($hostHandle -ne 0) {
        $metadata.hostMode = if ($NotepadWindowHandle -ne 0) { 'prepared-window' } else { 'benchmark-window' }
        if (@($before | Where-Object Handle -eq $hostHandle).Count -ne 1) {
            throw 'The Notepad host window is not an available Notepad window.'
        }
        # Notepad opens a file in its most recently active window.
        if (-not [LumiAutomationBench.NativeWindows]::Activate($hostHandle)) {
            throw 'Could not activate the benchmark Notepad host window; no file was opened.'
        }
    }
    [IO.File]::WriteAllText($metadata.filePath, "Replace this synthetic draft.`r`nDo not retain this line.",
        [Text.UTF8Encoding]::new($false))
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:WINDIR 'System32\notepad.exe'))
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $Directory
    $start.ArgumentList.Add($metadata.filePath)
    $launcher = [Diagnostics.Process]::Start($start)
    try { $metadata.launchProcessId = $launcher.Id }
    finally { $launcher.Dispose() }
    $previousHandles = @($before | ForEach-Object Handle)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 15) {
        $candidates = @(Get-NotepadWindows | Where-Object {
            $_.Title.IndexOf($Token, [StringComparison]::OrdinalIgnoreCase) -ge 0
        })
        if ($candidates.Count -gt 0) {
            $metadata.candidateWindows = @($candidates | ForEach-Object {
                [ordered]@{ processId = $_.ProcessId; handle = [string]$_.Handle; title = $_.Title }
            })
        }
        # The unique token proves the window shows this file; it must be new or the chosen host.
        if ($candidates.Count -eq 1 -and
            (($hostHandle -eq 0 -and $candidates[0].Handle -notin $previousHandles) -or $candidates[0].Handle -eq $hostHandle)) {
            $metadata.windowProcessId = $candidates[0].ProcessId
            $metadata.windowHandle = [string]$candidates[0].Handle
            $metadata.initialWindowTitles = @($candidates[0].Title)
            $metadata.windowTitles = @($candidates[0].Title)
            $metadata.ready = $true
            return $metadata
        }
        Start-Sleep -Milliseconds 200
    }
    $metadata.reason = 'Skipped: could not prove a single Notepad window showing this file. Launcher/window left untouched.'
    return $metadata
}

function Start-OwnedCalculator {
    $metadata = [ordered]@{ ready = $false; reason = $null; windowHandle = $null; windowProcessId = $null; selector = $null; closeRequested = $false }
    $running = @(Get-Process -Name CalculatorApp -ErrorAction SilentlyContinue)
    $windows = @([LumiAutomationBench.NativeWindows]::Snapshot() | Where-Object Title -ceq 'Calculator')
    foreach ($process in $running) { $process.Dispose() }
    if ($running.Count -gt 0 -or $windows.Count -gt 0) {
        $metadata.reason = 'Skipped: Calculator is already running; the user''s calculator is never touched.'
        return $metadata
    }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:WINDIR 'System32\calc.exe'))
    $start.UseShellExecute = $false
    $launchedAt = [DateTime]::Now.AddSeconds(-1)
    ([Diagnostics.Process]::Start($start)).Dispose()
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 20) {
        $windows = @([LumiAutomationBench.NativeWindows]::Snapshot() | Where-Object Title -ceq 'Calculator')
        if ($windows.Count -eq 1 -and $null -ne (Read-CalculatorDisplay $windows[0].Handle)) {
            $metadata.windowHandle = [string]$windows[0].Handle
            $metadata.windowProcessId = $windows[0].ProcessId
            $metadata.selector = 'hwnd:0x{0:X}' -f $windows[0].Handle
            $metadata.ready = $true
            return $metadata
        }
        Start-Sleep -Milliseconds 250
    }
    # No Calculator ran before the launch, so any instance started since then is the one launched here.
    foreach ($process in @(Get-Process -Name CalculatorApp -ErrorAction SilentlyContinue)) {
        try { if ($process.StartTime -ge $launchedAt) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue } }
        finally { $process.Dispose() }
    }
    $metadata.reason = 'Skipped: the launched Calculator window did not become available within 20 seconds; it was closed.'
    return $metadata
}

# Reads the display independently of Lumi, through the managed UI Automation client.
function Read-CalculatorDisplay {
    param([long]$Handle)
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $root = [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Handle)
        $condition = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::AutomationIdProperty, 'CalculatorResults')
        $display = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -eq $display) { return $null }
        return [string]$display.Current.Name
    }
    catch { return $null }
}
function Read-BridgeAudit {
    param([string]$ChatId, [string]$Directory)
    $messages = [Collections.Generic.List[object]]::new()
    $tools = [Collections.Generic.List[object]]::new()
    $offset = 0
    $chat = $null
    $expectedCount = $null
    do {
        $arguments = @{
            chatId = $ChatId; offset = $offset; maxMessages = 1000; sortDirection = 'asc'
            includeContent = $true; includeMetadata = $true; includeToolOutput = $true
            maxContentChars = 100000; maxToolOutputChars = 50000
        }
        $transcript = Invoke-Bridge 'read_transcript' $arguments
        Write-ArtifactJson (Join-Path $Directory "bridge-transcript-$offset.json") $transcript
        $arguments['sections'] = @('chat', 'summary', 'messages', 'toolcalls', 'errors')
        $activity = Invoke-Bridge 'read_activity' $arguments
        Write-ArtifactJson (Join-Path $Directory "bridge-activity-$offset.json") $activity
        $chat = $transcript['chat']
        if ($null -eq $expectedCount) { $expectedCount = [int]$transcript['totalMatched'] }
        if ([string]$chat['id'] -ne $ChatId -or [string]$activity['chat']['id'] -ne $ChatId -or
            [int]$transcript['totalMatched'] -ne $expectedCount -or
            [int]$activity['summary']['messageCount'] -ne $expectedCount) {
            throw 'Transcript changed during audit, or bridge returned the wrong chat.'
        }
        $page = @($transcript['messages'])
        foreach ($message in $page) { $messages.Add($message) }
        foreach ($tool in @($activity['toolCalls'])) { $tools.Add($tool) }
        $offset += $page.Count
        if ($offset -lt $expectedCount -and ($page.Count -eq 0 -or $offset -ge 20000)) {
            throw 'Could not collect a complete bounded transcript.'
        }
    } while ($offset -lt $expectedCount)
    $toolMessages = @($messages | Where-Object { $_['role'] -eq 'tool' })
    if ($messages.Count -ne $expectedCount -or $tools.Count -ne $toolMessages.Count) {
        throw 'Bridge transcript/activity counts disagree.'
    }
    return @{ messages = $messages.ToArray(); toolCalls = $tools.ToArray(); chat = $chat }
}

function Get-BridgePreview {
    param($Value, [int]$Limit)
    if ($null -eq $Value) { return $null }
    $text = ([string]$Value).Trim()
    if ($text.Length -le $Limit) { return $text }
    return $text.Substring(0, $Limit - 1).TrimEnd() + '...'
}

function Read-PersistedTranscript {
    param([string]$ChatId, [object[]]$BridgeMessages, [string]$Directory)
    # Never enumerate the user's chat directory or read any chat not created by this run.
    $file = Join-Path ([string]$script:bridge['appDataDir']) ('chats\' + ([guid]$ChatId).ToString() + '.json')
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $matchingRaw = $null
    $matchingMessages = $null
    do {
        try {
            $raw = [IO.File]::ReadAllText($file)
            if (-not $raw.TrimStart().StartsWith('[')) { throw 'Expected a per-chat message array.' }
            $messages = @($raw | ConvertFrom-Json -AsHashtable -Depth 100)
            $byId = @{}
            foreach ($message in $messages) { $byId[[string]$message['id']] = $message }
            $matches = $messages.Count -eq $BridgeMessages.Count -and $byId.Count -eq $messages.Count
            foreach ($message in $BridgeMessages) {
                $stored = $byId[[string]$message['id']]
                if ($null -eq $stored -or $stored['role'] -ne $message['role'] -or
                    $stored['toolStatus'] -ne $message['toolStatus'] -or
                    $stored['isStreaming'] -ne $message['isStreaming'] -or
                    (Get-BridgePreview $stored['content'] 100000) -cne $message['content'] -or
                    ($null -ne $message['toolOutput'] -and
                        (Get-BridgePreview $stored['toolOutput'] 50000) -cne $message['toolOutput'])) {
                    $matches = $false
                    break
                }
            }
            if ($matches) {
                $matchingRaw = $raw
                $matchingMessages = $messages
                $pendingDurations = @($messages | Where-Object {
                    $_['role'] -eq 'tool' -and $_['toolStatus'] -in @('Completed', 'Failed', 'Stopped') -and
                        $null -eq $_['toolDurationMs']
                })
                if ($pendingDurations.Count -eq 0) { break }
            }
        }
        catch { } # UI-owned persistence may still be flushing and holds FileShare.None.
        Start-Sleep -Milliseconds 200
    } while ($watch.Elapsed.TotalSeconds -lt 15)
    if ($null -eq $matchingRaw) {
        throw 'The new chat file did not converge to the full bridge transcript within 15 seconds.'
    }
    # Preserve a matching snapshot even if timing remains unavailable after the bounded wait.
    [IO.File]::WriteAllText((Join-Path $Directory 'persisted-transcript.json'),
        (Protect-Text $matchingRaw), [Text.UTF8Encoding]::new($false))
    return ,$matchingMessages
}

function Test-UIArguments {
    param($Arguments, [string]$TargetToken, $Violations)
    if ($Arguments -is [Collections.IDictionary]) {
        foreach ($key in $Arguments.Keys) {
            $value = $Arguments[$key]
            if ($key -in @('title', 'windowTitle') -and $value -is [string] -and
                $value.IndexOf($TargetToken, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                $Violations.Add("UI call targeted an unowned/non-unique window title: $value")
            }
            if (($key -eq 'keys' -or ($key -eq 'value' -and $Arguments['action'] -eq 'keys')) -and $value -is [string] -and
                $value -match '(?i)(^|[+ ])(win(dows)?|lwin|rwin|super)([+ ]|$)|Alt\+F4|Ctrl\+(W|F4)|Ctrl\+Shift\+Esc') {
                $Violations.Add("Disallowed desktop/launcher/close shortcut: $value")
            }
            Test-UIArguments $value $TargetToken $Violations
        }
    }
    elseif ($Arguments -is [array]) {
        foreach ($item in $Arguments) { Test-UIArguments $item $TargetToken $Violations }
    }
}

function Test-ToolPolicy {
    param([string]$Name, $Arguments, [string]$TargetToken, $Violations, [string]$Scenario = '')
    if ($Name -eq 'session_artifacts' -and $Arguments -is [Collections.IDictionary] -and
        $Arguments['path'] -ceq 'checkpoint.md' -and $Arguments['operation'] -in @('read', 'write', 'append')) {
        return
    }
    if ($Scenario -in @('vision', 'vision-gestures')) {
        if ($Name -notmatch '^(functions\.)?ui_(screenshot|click_at)$' -or
            $Arguments -isnot [Collections.IDictionary]) {
            $Violations.Add("Disallowed vision tool: $Name")
            return
        }
        if ($Name -match 'ui_screenshot$' -and
            ($Arguments['title'] -isnot [string] -or [string]::IsNullOrWhiteSpace($Arguments['title']))) {
            $Violations.Add('Vision screenshots must identify the unique fixture title.')
        }
        if ($Scenario -eq 'vision' -and $Name -match 'ui_click_at$' -and
            ($Arguments['allowForeground'] -isnot [bool] -or $Arguments['allowForeground'] -ne $true -or
                ($Arguments.Contains('clickCount') -and $Arguments['clickCount'] -ne 1) -or
                ($Arguments.Contains('button') -and $Arguments['button'] -cne 'left'))) {
            $Violations.Add('Vision requires one left click with explicit fixture-only foreground permission.')
        }
        if ($Scenario -eq 'vision-gestures' -and $Name -match 'ui_click_at$') {
            $button = if ($Arguments.Contains('button')) { $Arguments['button'] } else { 'left' }
            $count = if ($Arguments.Contains('clickCount')) { $Arguments['clickCount'] } else { 1 }
            $gestureAllowed = ($button -ceq 'left' -and $count -eq 2) -or ($button -ceq 'right' -and $count -eq 1)
            if (-not $gestureAllowed -or $Arguments['allowForeground'] -isnot [bool] -or $Arguments['allowForeground'] -ne $true) {
                $Violations.Add('Vision gestures permit only a left double-click or a single right-click with explicit fixture-only foreground permission.')
            }
        }
        Test-UIArguments $Arguments $TargetToken $Violations
        return
    }
    if ($Name -match '^(functions\.)?ui_[a-z0-9_]+$') {
        Test-UIArguments $Arguments $TargetToken $Violations
        return
    }
    if ($Name -eq 'multi_tool_use.parallel' -and $Arguments -is [Collections.IDictionary] -and
        $Arguments['tool_uses'] -is [array] -and $Arguments['tool_uses'].Count -gt 0) {
        foreach ($call in $Arguments['tool_uses']) {
            Test-ToolPolicy ([string]$call['recipient_name']) $call['parameters'] $TargetToken $Violations $Scenario
        }
        return
    }
    $Violations.Add("Disallowed tool: $Name")
}

function Get-FieldChecks {
    param($Expected, $Actual)
    foreach ($field in $Expected.Keys) {
        $present = $null -ne $Actual -and $Actual.Contains($field)
        $value = if ($present) { $Actual[$field] } else { $null }
        $equal = if ($Expected[$field] -is [string]) {
            $value -is [string] -and [string]::Equals($value, $Expected[$field], [StringComparison]::Ordinal)
        }
        elseif ($Expected[$field] -is [bool]) { $value -is [bool] -and $value -eq $Expected[$field] }
        else { $value -eq $Expected[$field] }
        [ordered]@{ field = $field; expected = $Expected[$field]; actual = $value; passed = ($present -and $equal) }
    }
}

$commonPrompt = @'
Perform this Windows desktop task efficiently. Reuse known targets and batch UI actions when supported.
Use exclusively ui_* tools for computer operations. A batching wrapper is allowed only when every wrapped call is a ui_* call.
Do not use code execution, shell commands, scripts, browser tools, file APIs, subagents, other chats, or other tools for the task. Internal session_artifacts bookkeeping on checkpoint.md is allowed. Do not launch programs through keyboard shortcuts or address bars.
Work only in the uniquely identified target window and its own dialogs. Do not inspect, edit, close, or otherwise interact with other windows, including Explorer and Lumi. Do not close the target window.
This is a disposable test window: allowForeground=true is permitted for this window only if a background-native action is unavailable.
Use window titles containing the supplied unique target name, not hwnd selectors, so the benchmark can audit window ownership after dialogs have closed.
Complete the task, then reply with a brief completion statement. Do not ask questions.

'@
# Keep source ASCII so both Windows PowerShell 5.1 and pwsh agree on the task.
$documentText = "Caf$([char]0x00E9) review`nOrder REF-7328 $([char]0x2014) confirmed.`nTotal: $([char]0x20AC)42"
$definitions = [ordered]@{
    order = @{
        task = @'
In the window titled "{TARGET}", use the Order tab to enter:
First name: Morgan
Last name: Reed
Email: morgan.reed@example.test
Company: Northwind Demo
City: Bristol
Product: Travel mug
Quantity: 4
Delivery: Express
Enable Send receipt, then click Submit order.
'@
        expected = [ordered]@{
            targetOpen = $true; submitted = $true; firstName = 'Morgan'; lastName = 'Reed'; email = 'morgan.reed@example.test'
            company = 'Northwind Demo'; city = 'Bristol'; product = 'Travel mug'; quantity = 4
            delivery = 'Express'; sendReceipt = $true
        }
    }
    preferences = @{
        task = @'
In the window titled "{TARGET}", open Preferences. In its Preferences dialog set:
Display name: Morgan R.
Language: French
Theme: Dark
Reminder time: 09:30
Enable Compact layout and disable Desktop notifications.
Click Save preferences to save these settings and return to the main window.
'@
        expected = [ordered]@{
            targetOpen = $true; saved = $true; displayName = 'Morgan R.'; language = 'French'; theme = 'Dark'
            reminderTime = '09:30'; compactLayout = $true; desktopNotifications = $false
        }
    }
    catalog = @{
        task = @'
In the window titled "{TARGET}", open the Catalog tab.
Find and select the item "SKU-187 - Cedar travel case" in the catalog, then click Use selected item.
'@
        expected = [ordered]@{
            targetOpen = $true; selectedId = 'SKU-187'; selectedLabel = 'SKU-187 - Cedar travel case'
            committedId = 'SKU-187'; committedLabel = 'SKU-187 - Cedar travel case'
        }
    }
    notepad = @{
        task = @'
In the Notepad window whose title contains "{TARGET}", replace the entire document with exactly these three lines:
Lumi desktop benchmark
Order BR-187 is confirmed.
Quantity: 4 travel mugs.
There must be no extra blank line or newline after the third line.
Save the document in place with Ctrl+S. Do not use Save As or change tabs.
'@
        expected = [ordered]@{
            targetOpen = $true; fileExists = $true; utf8Valid = $true
            normalizedText = "Lumi desktop benchmark`nOrder BR-187 is confirmed.`nQuantity: 4 travel mugs."
        }
    }
    document = @{
        task = @'
In the window titled "{TARGET}", open the Workbench tab and enter exactly these three lines in Document text:
{DOCUMENT_TEXT}
There must be no extra blank line or newline after the third line.
Use File > Save as (Ctrl+Shift+S) to open the Windows Save As dialog. Set its File name to automation-note.txt and save only in the directory initially shown by that dialog. Do not navigate to another directory or replace an existing file.
'@.Replace('{DOCUMENT_TEXT}', $documentText)
        expected = [ordered]@{
            targetOpen = $true; saved = $true; saveCount = 1; fileName = 'automation-note.txt'
            filePathMatches = $true; fileExists = $true; utf8Valid = $true
            normalizedText = $documentText; lineCount = 3; encoding = 'utf-8'
        }
    }
    delayed = @{
        task = @'
In the window titled "{TARGET}", open the Workflow tab.
Click Load report, wait and observe until the report is ready and Apply report becomes available, then click Apply report once.
Verify that the report was applied.
'@
        expected = [ordered]@{
            targetOpen = $true; loadCount = 1; loadCompleted = $true
            reportText = 'Report ready: 12 orders; total 480.'; applied = $true; applyCount = 1
        }
    }
    scroll = @{
        task = @'
In the window titled "{TARGET}", open the Scroll tab.
Scroll the Report entries list all the way to the end until "Entry 200 - End of report" is fully visible.
Then click Confirm end once and verify that the end was acknowledged. Merely selecting an offscreen item is not sufficient; actually scroll the list.
'@
        expected = [ordered]@{
            targetOpen = $true; totalRows = 200; scrollObserved = $true; lastRowVisible = $true
            confirmed = $true; confirmCount = 1; confirmedLastRowVisible = $true; scrollPositionVerified = $true
        }
    }
    transfer = @{
        task = @'
In the window titled "{TARGET}", open the Transfer tab and read the Source reference displayed there.
Click Open transfer to open this fixture's separate Transfer window. Copy the reference you read into Destination reference in that window, then click Commit once.
Verify that the transfer was saved. Leave both fixture windows open.
'@
        expected = [ordered]@{
            targetOpen = $true; opened = $true; windowOpen = $true; sourceReference = 'REF-7328'
            destinationReference = 'REF-7328'; committed = $true; commitCount = 1
        }
    }
    vision = @{
        task = @'
Perform this Windows desktop task efficiently in the disposable window titled "{TARGET}".
Use only ui_screenshot and ui_click_at for this task. Internal session_artifacts bookkeeping on checkpoint.md is allowed.
Do not use UI Automation inspection, ui_list_windows, other ui_* tools, batching wrappers, code execution, shell commands, scripts, file APIs, browser tools, subagents, or other chats.
Only capture and interact with this uniquely identified fixture window. Use its title, not hwnd selectors. Do not inspect other windows, launch programs, or close the fixture.
Read the random code painted in the window. The goal is exactly one successful activation of the painted blue Confirm target, not exactly one tool invocation. Use image-relative coordinates from a fresh screenshot for each single left-click attempt.
You are authorized to use allowForeground=true only for this fixture's coordinate clicks. After each attempt, take a fresh screenshot and inspect the painted state. Input sent is not proof that the application acted.
If the fresh post-attempt image clearly shows the target is still unconfirmed, or the previous call explicitly reports rejection with no click sent, you may retry once using a new capture. Make at most two coordinate-click attempts in total; never reuse an earlier capture.
Never click again after a screenshot shows Confirmed. If neither retry condition is established, do not send another click.
Reply with the exact code you read and a brief result. Report completion only after visually verifying Confirmed; otherwise explain what remains unconfirmed. Do not ask questions.
'@
        expected = [ordered]@{
            targetOpen = $true; confirmed = $true; clicks = 1; outsideClicks = 0
            clickAttemptsWithinBudget = $true; freshCaptureBeforeEachAttempt = $true; captureIdsUniqueAndNonempty = $true
            screenshotBeforeClick = $true; screenshotAfterClick = $true; finalReplyContainsCode = $true
        }
    }
    'vision-gestures' = @{
        task = @'
Perform this Windows desktop task efficiently in the disposable window titled "{TARGET}".
Use only ui_screenshot and ui_click_at for this task. Internal session_artifacts bookkeeping on checkpoint.md is allowed.
Do not use UI Automation inspection, ui_list_windows, other ui_* tools, batching wrappers, code execution, shell commands, scripts, file APIs, browser tools, subagents, or other chats.
Only capture and interact with this uniquely identified fixture window. Use its title, not hwnd selectors. Do not inspect other windows, launch programs, or close the fixture.
The goal is exactly one successful left double-click activation of the painted green Double click target, then exactly one successful right-click activation of the painted orange Right click target. Leave the blue Confirm target untouched.
Use image-relative coordinates from a fresh screenshot for each gesture. You are authorized to use allowForeground=true only for this fixture's coordinate clicks.
After each attempt, take a fresh screenshot and read the painted counters. Input sent is not proof that the application acted.
You may make at most one additional attempt in total across both gestures, for at most three coordinate-click attempts overall. Retry only the missing gesture, and only if its counter explicitly remains 0 in a fresh post-attempt image. Use a new capture; never reuse an earlier one.
Never repeat a gesture whose counter already reads 1. If the counters are unclear, do not resend input. Take a final screenshot and verify that the painted feedback reads "Double: 1" and "Right: 1".
Reply with a brief result. Report completion only after visually verifying both counters are 1; otherwise explain which action remains unconfirmed. Do not ask questions.
'@
        expected = [ordered]@{
            targetOpen = $true; gesturesEnabled = $true; clicks = 0; outsideClicks = 0
            doubleClicks = 1; rightClicks = 1
            clickAttemptsWithinBudget = $true; freshCaptureBeforeEachAttempt = $true; captureIdsUniqueAndNonempty = $true
            screenshotBeforeClick = $true; screenshotBetweenClicks = $true; screenshotAfterClick = $true
        }
    }
    tree = @{
        task = @'
In the window titled "{TARGET}", open the Tree tab. In the Locations tree, select the city Hamburg under Europe > Germany, then click Assign location.
Verify that the location was assigned.
'@
        expected = [ordered]@{ targetOpen = $true; assignedPath = 'Europe/Germany/Hamburg'; assignCount = 1; checkCount = 0 }
    }
    grid = @{
        task = @'
In the window titled "{TARGET}", open the Grid tab. In the Invoices grid, find invoice INV-1042 and change its Status from Open to Paid, then click Save changes.
Reply with that invoice's customer and amount.
'@
        expected = [ordered]@{
            targetOpen = $true; saveCount = 1; changedList = 'INV-1042'; targetStatus = 'Paid'
            replyHasCustomer = $true; replyHasAmount = $true
        }
    }
    wizard = @{
        task = @'
In the window titled "{TARGET}", open the Setup tab and click Start setup.
Complete the setup wizard with the Pro plan, 12 seats and 250 GB of storage, accept the terms, and finish.
'@
        expected = [ordered]@{
            targetOpen = $true; completed = $true; finishCount = 1; cancelled = $false
            plan = 'Pro'; seats = 12; storageGb = 250; acceptedTerms = $true
        }
    }
    recovery = @{
        task = @'
In the window titled "{TARGET}", open the Account tab and create an account with username morgan_r, email morgan.reed@example.test and age 34.
If the app reports that the username is unavailable, use the alternative it suggests and create the account.
'@
        expected = [ordered]@{
            targetOpen = $true; created = $true; createdCount = 1; rejectedCount = 1
            username = 'morgan_r7'; email = 'morgan.reed@example.test'; age = 34
        }
    }
    'context-menu' = @{
        task = @'
In the window titled "{TARGET}", open the Files tab. Archive the file report-q3.xlsx using its right-click context menu, and confirm the prompt.
Do not archive, delete or open any other file.
'@
        expected = [ordered]@{
            targetOpen = $true; archivedList = 'report-q3.xlsx'; archiveCount = 1
            deleteCount = 0; declinedCount = 0; openCount = 0
        }
    }
    wpf = @{
        task = @'
In the window titled "{TARGET}", select "Order 1873 - Harbor Books" in the Orders list, set Discount (%) to 15, open Advanced options, enable Priority handling, choose the York warehouse, then click Apply dispatch.
Verify that the dispatch was applied.
'@
        expected = [ordered]@{
            targetOpen = $true; applied = $true; applyCount = 1; selectedOrder = 'Order 1873 - Harbor Books'
            discount = 15; priority = $true; warehouse = 'York'
        }
    }
    calculator = @{
        task = @'
In the Windows Calculator window {TARGET} (use exactly this hwnd selector as the window title argument), use the calculator's own buttons to clear it and calculate 1234 x 56 - 789.
Reply with the result shown on its display. Do not change the calculator's mode, history, memory or settings, and do not close it.
'@
        expected = [ordered]@{ targetOpen = $true; displayDigits = '68315'; replyHasResult = $true }
    }
}

$runResults = [Collections.Generic.List[object]]::new()
# Real apps have fixed titles, so they are identified by the hwnd selector of the window the runner launched.
$realAppPrompt = $commonPrompt.Replace(
    'Use window titles containing the supplied unique target name, not hwnd selectors, so the benchmark can audit window ownership after dialogs have closed.',
    'Identify the target only by the supplied hwnd selector; the benchmark launched that window for this task.')
foreach ($iteration in 1..$Iterations) {
    foreach ($scenario in $Scenarios) {
        $caseId = '{0:D3}-{1}' -f $iteration, $scenario
        $caseDirectory = Join-Path $runRoot $caseId
        [void][IO.Directory]::CreateDirectory($caseDirectory)
        $template = if ($scenario -in @('vision', 'vision-gestures')) { $definitions[$scenario].task }
            elseif ($scenario -eq 'calculator') { $realAppPrompt + $definitions[$scenario].task }
            else { $commonPrompt + $definitions[$scenario].task }
        $template = $template.Replace("`r`n", "`n")
        $caseFixtureHash = if ($scenario -in @('vision', 'vision-gestures')) { $visionFixtureHash }
            elseif ($scenario -eq 'wpf') { $wpfFixtureHash }
            elseif ($scenario -in $realApps) { 'real-app' }
            else { $fixtureHash }
        $contract = [ordered]@{
            suiteVersion = 'windows-computer-use-v3'; scenario = $scenario
            promptTemplate = $template; expected = $definitions[$scenario].expected
            model = $Model; reasoningEffort = $ReasoningEffort; contextWindowTier = 'default'
            timeoutSeconds = $TimeoutSeconds; fixtureSha256 = $caseFixtureHash; runnerSha256 = $runnerHash
            notepadTargetMode = if ($scenario -eq 'notepad' -and $NotepadWindowHandle -ne 0) { 'prepared-window' } else { 'launch' }
        }
        $runResults.Add([ordered]@{
            caseId = $caseId; iteration = $iteration; scenario = $scenario; label = $Label
            comparisonKey = (Get-TextHash (ConvertTo-Json -InputObject $contract -Depth 20 -Compress))
            fixtureSha256 = $caseFixtureHash
            model = $Model; reasoningEffort = $ReasoningEffort; contextWindowTier = 'default'
            notepadWindowHandle = $NotepadWindowHandle
            timeoutSeconds = $TimeoutSeconds; promptTemplate = $template; prompt = $null; targetToken = $null
            chatId = $null; directory = $caseDirectory; status = 'not-run'; success = $false
            sentAt = $null; completedAt = $null; idleObserved = $false; elapsedMs = $null
            toolCallCount = $null; totalToolDurationMs = $null; knownToolDurationMs = $null
            timedToolCallCount = 0; missingToolDurationCount = 0; toolOutputChars = $null
            knownToolOutputChars = $null; availableToolOutputCount = 0; missingToolOutputCount = 0
            toolOutputsComplete = $false; toolMeasurements = [Collections.Generic.List[object]]::new()
            actualModelLabels = @(); actualReasoningEfforts = @(); actualChatModel = $null
            actualChatReasoningEffort = $null; transcriptComplete = $false
            expected = $definitions[$scenario].expected; actual = $null; checks = @(); assertionPassed = $false
            target = $null; fixtureCleanedUp = $null; fixtureRetainedForActiveChat = $false
            errorFlags = [ordered]@{
                notRun = $true; skipped = $false; setupError = $false; bridgeError = $false
                timedOut = $false; incompleteTranscript = $false; missingToolTiming = $false
                toolFailed = $false; chatError = $false; policyViolation = $false
                modelMismatch = $false; assertionFailed = $false; unsafeSession = $false; cleanupError = $false
            }
            errors = [Collections.Generic.List[string]]::new()
            policyViolations = [Collections.Generic.List[string]]::new()
        })
    }
}
$bundle = [ordered]@{
    schemaVersion = 1; suiteVersion = 'windows-computer-use-v3'; suiteId = $suiteId; label = $Label
    startedAt = [DateTimeOffset]::UtcNow.ToString('o'); finishedAt = $null
    lumiProcessId = $LumiProcessId; bridgeInstanceId = $null
    parameters = [ordered]@{
        iterations = $Iterations; scenarios = $Scenarios; timeoutSeconds = $TimeoutSeconds
        model = $Model; reasoningEffort = $ReasoningEffort; contextWindowTier = 'default'
    }
    environment = [ordered]@{
        osVersion = [Environment]::OSVersion.VersionString; runnerPowerShell = $PSVersionTable.PSVersion.ToString()
        primaryScreenWidth = [LumiAutomationBench.NativeWindows]::GetSystemMetrics(0)
        primaryScreenHeight = [LumiAutomationBench.NativeWindows]::GetSystemMetrics(1)
    }
    fixtureSha256 = $fixtureHash; visionFixtureSha256 = $visionFixtureHash; wpfFixtureSha256 = $wpfFixtureHash; runnerSha256 = $runnerHash
    abortedReason = $null; summary = $null; results = $runResults
}
function Save-Results {
    $passed = @($runResults | Where-Object { $_.success }).Count
    $bundle.summary = [ordered]@{
        planned = $runResults.Count; passed = $passed
        failed = @($runResults | Where-Object { $_.status -eq 'failed' }).Count
        skipped = @($runResults | Where-Object { $_.status -eq 'skipped' }).Count
        notRun = @($runResults | Where-Object { $_.status -eq 'not-run' }).Count
        successRatePercent = [Math]::Round(100.0 * $passed / $runResults.Count, 2)
    }
    Write-ArtifactJson (Join-Path $runRoot 'results.json') $bundle
}

$mutex = [Threading.Mutex]::new($false, 'Local\Lumi.UIAutomationBenchmarks.Desktop')
$hasMutex = $false
try {
    Save-Results
    try { $hasMutex = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $hasMutex = $true }
    if (-not $hasMutex) { throw 'Another benchmark runner owns this desktop.' }
    $script:bridge = Find-DebugBridge
    $script:bridgeToken = [string]$script:bridge['token']
    $bundle.bridgeInstanceId = $script:bridge['instanceId']
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $script:http = [Net.Http.HttpClient]::new($handler)
    $script:http.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    Assert-LumiIdle

    foreach ($result in $runResults) {
        if ($bundle.abortedReason) {
            $result.errors.Add('Not attempted: ' + $bundle.abortedReason)
            Write-ArtifactJson (Join-Path $result.directory 'result.json') $result
            continue
        }
        $fixtureProcess = $null
        $fixtureStartTicks = $null
        $watch = $null
        $sendAttempted = $false
        $sessionQuiescent = $false
        $tools = @()
        $assistantMessages = @()
        $stage = 'setup'
        $result.status = 'running'
        $result.errorFlags.notRun = $false
        $result.targetToken = 'Lumi-UI-Bench-' + [guid]::NewGuid().ToString('N')
        $result.prompt = $result.promptTemplate.Replace('{TARGET}', $result.targetToken)
        Write-ArtifactJson (Join-Path $result.directory 'prompt.json') @{
            template = $result.promptTemplate; message = $result.prompt
            model = $Model; reasoningEffort = $ReasoningEffort; contextWindowTier = 'default'
        }
        try {
            $stage = 'bridge'
            Assert-LumiIdle
            $sessionQuiescent = $true
            $stage = 'setup'
            if ($result.scenario -in $interactiveScenarios -and -not [LumiAutomationBench.NativeWindows]::HasPhysicalInput()) {
                $result.target = [ordered]@{
                    ready = $false
                    reason = 'Skipped: this scenario needs a connected, unlocked desktop (session disconnected or locked, or no foreground window); no target or chat was created.'
                }
                $result.status = 'skipped'
                $result.errorFlags.skipped = $true
                $result.errors.Add($result.target.reason)
                continue
            }
            if ($result.scenario -in $realApps) {
                $result.target = if ($result.scenario -eq 'notepad') { Start-OwnedNotepad $result.directory $result.targetToken }
                    else { Start-OwnedCalculator }
                if (-not $result.target.ready) {
                    $result.status = 'skipped'
                    $result.errorFlags.skipped = $true
                    $result.errors.Add($result.target.reason)
                    continue
                }
                if ($result.scenario -eq 'calculator') {
                    $result.targetToken = $result.target.selector
                    $result.prompt = $result.promptTemplate.Replace('{TARGET}', $result.targetToken)
                    Write-ArtifactJson (Join-Path $result.directory 'prompt.json') @{
                        template = $result.promptTemplate; message = $result.prompt
                        model = $Model; reasoningEffort = $ReasoningEffort; contextWindowTier = 'default'
                    }
                }
            }
            else {
                $statePath = Join-Path $result.directory 'fixture-state.json'
                $caseFixtureScript = if ($result.scenario -in @('vision', 'vision-gestures')) { $visionFixtureScript }
                    elseif ($result.scenario -eq 'wpf') { $wpfFixtureScript }
                    else { $fixtureScript }
                $start = [Diagnostics.ProcessStartInfo]::new($windowsPowerShell)
                $start.UseShellExecute = $false
                $start.CreateNoWindow = $true
                $start.WorkingDirectory = $result.directory
                foreach ($argument in @('-NoProfile', '-NonInteractive', '-STA', '-ExecutionPolicy', 'Bypass',
                    '-WindowStyle', 'Hidden', '-File', $caseFixtureScript, '-Title', $result.targetToken, '-StatePath', $statePath)) {
                    $start.ArgumentList.Add($argument)
                }
                if ($result.scenario -eq 'vision-gestures') { $start.ArgumentList.Add('-Gestures') }
                $fixtureProcess = [Diagnostics.Process]::Start($start)
                $fixtureStartTicks = $fixtureProcess.StartTime.ToFileTimeUtc()
                $result.target = [ordered]@{
                    processId = $fixtureProcess.Id; title = $result.targetToken; statePath = $statePath
                    startedAt = $fixtureProcess.StartTime.ToUniversalTime().ToString('o')
                    fixtureScript = [IO.Path]::GetFileName($caseFixtureScript)
                }
                $ready = $false
                $startup = [Diagnostics.Stopwatch]::StartNew()
                while ($startup.Elapsed.TotalSeconds -lt 15 -and -not $fixtureProcess.HasExited) {
                    try {
                        $state = [IO.File]::ReadAllText($statePath) | ConvertFrom-Json -AsHashtable
                        $ready = $state['ready'] -eq $true -and $state['title'] -ceq $result.targetToken -and
                            [int]$state['processId'] -eq $fixtureProcess.Id
                    }
                    catch { $ready = $false }
                    if ($ready) { break }
                    Start-Sleep -Milliseconds 100
                }
                if (-not $ready) { throw 'Owned fixture did not become ready within 15 seconds.' }
            }

            $stage = 'bridge'
            $created = Invoke-Bridge 'create_chat' @{
                title = "UI benchmark $Label $($result.caseId) $($suiteId.Substring(0, 8))"
                model = $Model; reasoningEffort = $ReasoningEffort; contextWindowTier = 'default'; open = $true
            }
            $result.chatId = ([guid]$created['chat']['id']).ToString()
            Write-ArtifactJson (Join-Path $result.directory 'chat-created.json') $created
            if ([int]$created['chat']['messageCount'] -ne 0 -or
                $created['chat']['lastModelUsed'] -cne $Model -or
                $created['chat']['lastReasoningEffortUsed'] -cne $ReasoningEffort) {
                throw 'New chat did not retain the requested empty state/model/effort.'
            }
            Save-Results
            $sessionQuiescent = $false
            $sendAttempted = $true
            $result.sentAt = [DateTimeOffset]::UtcNow.ToString('o')
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $sent = Invoke-Bridge 'send_message' @{
                chatId = $result.chatId; message = $result.prompt; waitForIdle = $false
            } ($TimeoutSeconds * 1000)
            if ($sent['sent'] -ne $true -or [string]$sent['chat']['id'] -ne $result.chatId) {
                throw 'Send did not confirm this newly created chat.'
            }
            $remainingMs = [int][Math]::Floor($TimeoutSeconds * 1000 - $watch.Elapsed.TotalMilliseconds)
            if ($remainingMs -le 0) { throw [TimeoutException]::new('Send exhausted the scenario deadline.') }
            $idle = Invoke-Bridge 'wait_for_idle' @{
                chatId = $result.chatId; timeoutMs = [Math]::Max(1, $remainingMs - 100); pollIntervalMs = 100
            } $remainingMs
            $watch.Stop()
            $result.elapsedMs = [Math]::Round($watch.Elapsed.TotalMilliseconds, 2)
            $result.completedAt = [DateTimeOffset]::UtcNow.ToString('o')
            $result.idleObserved = $idle['idle'] -eq $true -and [string]$idle['chat']['id'] -eq $result.chatId
            Write-ArtifactJson (Join-Path $result.directory 'send-result.json') $sent
            Write-ArtifactJson (Join-Path $result.directory 'idle-result.json') $idle
            if (-not $result.idleObserved) { throw 'Idle was not confirmed for this chat.' }
        }
        catch {
            if ($stage -eq 'bridge') {
                $result.errorFlags.bridgeError = $true
                $bundle.abortedReason = 'Bridge/send/idle could not be confirmed; inspect the recorded chat before another desktop run.'
            }
            else { $result.errorFlags.setupError = $true }
            $result.errorFlags.timedOut = $_.Exception -is [TimeoutException] -or
                ($null -ne $watch -and $watch.Elapsed.TotalSeconds -ge $TimeoutSeconds)
            $result.errors.Add((Protect-Text $_.Exception.Message))
        }
        finally {
            if ($null -ne $watch -and $watch.IsRunning) {
                $watch.Stop()
                $result.elapsedMs = [Math]::Round($watch.Elapsed.TotalMilliseconds, 2)
            }
            if ($sendAttempted) {
                try {
                    $audit = Read-BridgeAudit $result.chatId $result.directory
                    $messages = Read-PersistedTranscript $result.chatId $audit.messages $result.directory
                    $result.transcriptComplete = $result.idleObserved
                    $result.errorFlags.incompleteTranscript = -not $result.transcriptComplete
                    $tools = @($messages | Where-Object { $_['role'] -eq 'tool' })
                    $result.toolCallCount = $tools.Count
                    $result.knownToolDurationMs = 0.0
                    foreach ($tool in $tools) {
                        $duration = $tool['toolDurationMs']
                        $durationAvailable = $null -ne $duration -and [double]::IsFinite([double]$duration) -and [double]$duration -ge 0
                        if ($durationAvailable) {
                            $result.knownToolDurationMs += [double]$duration
                            $result.timedToolCallCount++
                        }
                        else { $result.missingToolDurationCount++ }
                        $outputAvailable = $null -ne $tool['toolOutput']
                        $outputChars = $null
                        if ($outputAvailable) {
                            $outputChars = ([string]$tool['toolOutput']).Length
                            if ($null -eq $result.knownToolOutputChars) { $result.knownToolOutputChars = 0L }
                            $result.knownToolOutputChars += $outputChars
                            $result.availableToolOutputCount++
                        }
                        else { $result.missingToolOutputCount++ }
                        $result.toolMeasurements.Add([ordered]@{
                            toolCallId = $tool['toolCallId']; toolName = $tool['toolName']; status = $tool['toolStatus']
                            toolStartedAt = $tool['toolStartedAt']
                            durationAvailable = $durationAvailable
                            durationMs = if ($durationAvailable) { [double]$duration } else { $null }
                            durationSource = if ($durationAvailable) { 'persisted-chat-file' } else { $null }
                            outputAvailable = $outputAvailable; outputChars = $outputChars
                            outputSource = if ($outputAvailable) { 'persisted-chat-file' } else { $null }
                        })
                        if ($tool['toolStatus'] -ne 'Completed') { $result.errorFlags.toolFailed = $true }
                        $arguments = $null
                        try { $arguments = ([string]$tool['content']) | ConvertFrom-Json -AsHashtable -Depth 100 }
                        catch { $result.policyViolations.Add("Unparseable arguments for $($tool['toolName']).") }
                        Test-ToolPolicy ([string]$tool['toolName']) $arguments $result.targetToken $result.policyViolations $result.scenario
                    }
                    $result.errorFlags.missingToolTiming = $result.missingToolDurationCount -gt 0
                    if (-not $result.errorFlags.missingToolTiming) {
                        $result.totalToolDurationMs = [Math]::Round($result.knownToolDurationMs, 2)
                    }
                    $result.toolOutputsComplete = $result.transcriptComplete -and $result.missingToolOutputCount -eq 0
                    if ($result.toolOutputsComplete) {
                        $result.toolOutputChars = if ($tools.Count -eq 0) { 0L } else { $result.knownToolOutputChars }
                    }
                    if ($tools.Count -eq 0) { $result.policyViolations.Add('No computer-use tool calls were recorded.') }
                    $userMessages = @($messages | Where-Object { $_['role'] -eq 'user' })
                    $assistantMessages = @($messages | Where-Object { $_['role'] -eq 'assistant' })
                    if ($userMessages.Count -ne 1 -or $userMessages[0]['content'] -cne $result.prompt -or
                        $assistantMessages.Count -eq 0) {
                        $result.policyViolations.Add('Expected exactly the original prompt and at least one assistant reply.')
                    }
                    $result.errorFlags.chatError = @($messages | Where-Object { $_['role'] -eq 'error' }).Count -gt 0
                    $result.actualModelLabels = @($assistantMessages | ForEach-Object { $_['model'] } |
                        Where-Object { $_ } | Sort-Object -Unique)
                    $result.actualReasoningEfforts = @($messages | ForEach-Object { $_['reasoningEffort'] } |
                        Where-Object { $_ } | Sort-Object -Unique)
                    $result.actualChatModel = $audit.chat['lastModelUsed']
                    $result.actualChatReasoningEffort = $audit.chat['lastReasoningEffortUsed']
                    $result.errorFlags.modelMismatch = $result.actualModelLabels.Count -eq 0 -or
                        $result.actualReasoningEfforts.Count -eq 0 -or
                        @($result.actualModelLabels | Where-Object { $_ -cne $Model }).Count -gt 0 -or
                        @($result.actualReasoningEfforts | Where-Object { $_ -cne $ReasoningEffort }).Count -gt 0 -or
                        $result.actualChatModel -cne $Model -or $result.actualChatReasoningEffort -cne $ReasoningEffort
                    $result.errorFlags.policyViolation = $result.policyViolations.Count -gt 0
                    if ($result.errorFlags.policyViolation) {
                        $bundle.abortedReason = 'Tool/target policy violation; inspect the transcript before another desktop run.'
                    }
                    Assert-LumiIdle
                    $sessionQuiescent = $true
                }
                catch {
                    $result.errorFlags.incompleteTranscript = -not $result.transcriptComplete
                    $result.errorFlags.unsafeSession = $true
                    $result.errors.Add((Protect-Text $_.Exception.Message))
                    $bundle.abortedReason = 'Audit or session quiescence could not be confirmed; no further cases scheduled.'
                }
                try {
                    if ($result.scenario -eq 'notepad') {
                        $file = [string]$result.target.filePath
                        $actual = [ordered]@{
                            targetOpen = $false; fileExists = [IO.File]::Exists($file); utf8Valid = $false; normalizedText = $null
                            byteLength = $null; sha256 = $null; hasUtf8Bom = $false; rawUtf8Base64 = $null
                        }
                        $result.actual = $actual
                        $result.target.windowTitles = @([LumiAutomationBench.NativeWindows]::Snapshot() |
                            Where-Object { $_.ProcessId -eq $result.target.windowProcessId -and
                                [string]$_.Handle -eq $result.target.windowHandle -and
                                $_.Title.IndexOf($result.targetToken, [StringComparison]::OrdinalIgnoreCase) -ge 0 } |
                            Select-Object -ExpandProperty Title)
                        $actual.targetOpen = $result.target.windowTitles.Count -eq 1
                        if ($actual.fileExists) {
                            $bytes = [IO.File]::ReadAllBytes($file)
                            $actual.byteLength = $bytes.Length
                            $actual.sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
                            $actual.rawUtf8Base64 = [Convert]::ToBase64String($bytes)
                            $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
                            $actual.utf8Valid = $true
                            $actual.hasUtf8Bom = $text.StartsWith([string][char]0xFEFF, [StringComparison]::Ordinal)
                            if ($actual.hasUtf8Bom) { $text = $text.Substring(1) }
                            $actual.normalizedText = $text.Replace("`r`n", "`n").Replace("`r", "`n")
                        }
                    }
                    elseif ($result.scenario -eq 'calculator') {
                        $display = Read-CalculatorDisplay ([long]$result.target.windowHandle)
                        $digits = if ($null -ne $display) { $display -replace '[^0-9]', '' } else { $null }
                        $finalReply = if ($assistantMessages.Count -gt 0) { [string]$assistantMessages[-1]['content'] } else { '' }
                        $result.actual = [ordered]@{
                            targetOpen = $null -ne $display; display = $display; displayDigits = $digits
                            finalAssistantReply = $finalReply
                            replyHasResult = $finalReply -match '(?<![0-9,])68,?315(?![0-9])'
                        }
                    }
                    else {
                        $state = [IO.File]::ReadAllText($result.target.statePath) | ConvertFrom-Json -AsHashtable
                        if ($state['title'] -cne $result.targetToken -or [int]$state['processId'] -ne $result.target.processId) {
                            throw 'Fixture state identity does not match the owned process.'
                        }
                        if ($result.scenario -in @('vision', 'vision-gestures')) {
                            $visualCode = [string]$state['visualCode']
                            $finalReply = if ($assistantMessages.Count -gt 0) { [string]$assistantMessages[-1]['content'] } else { '' }
                            $clickToolCalls = 0
                            $doubleClickToolCalls = 0
                            $rightClickToolCalls = 0
                            $requiredClickActions = if ($result.scenario -eq 'vision-gestures') { 2 } else { 1 }
                            $clickAttemptLimit = $requiredClickActions + 1
                            $captureIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                            $captureIdsUniqueAndNonempty = $true
                            $freshCaptureAvailable = $false
                            $freshCaptureBeforeEachAttempt = $true
                            $screenshotBeforeClick = $false
                            $screenshotBetweenClicks = $false
                            $screenshotAfterClick = $false
                            foreach ($tool in $tools) {
                                if ($tool['toolName'] -match '^(functions\.)?ui_click_at$') {
                                    if ($clickToolCalls -gt 0 -and $freshCaptureAvailable) { $screenshotBetweenClicks = $true }
                                    $clickToolCalls++
                                    if (-not $freshCaptureAvailable) { $freshCaptureBeforeEachAttempt = $false }
                                    $freshCaptureAvailable = $false
                                    $screenshotAfterClick = $false
                                    $clickArguments = ([string]$tool['content']) | ConvertFrom-Json -AsHashtable -Depth 100
                                    if ($clickArguments -isnot [Collections.IDictionary]) { throw 'Vision click arguments are not a JSON object.' }
                                    $captureId = $clickArguments['captureId']
                                    if ($captureId -isnot [string] -or [string]::IsNullOrWhiteSpace($captureId) -or
                                        -not $captureIds.Add($captureId)) {
                                        $captureIdsUniqueAndNonempty = $false
                                    }
                                    if ($result.scenario -eq 'vision-gestures') {
                                        $button = if ($clickArguments.Contains('button')) { $clickArguments['button'] } else { 'left' }
                                        $count = if ($clickArguments.Contains('clickCount')) { $clickArguments['clickCount'] } else { 1 }
                                        if ($button -ceq 'left' -and $count -eq 2) { $doubleClickToolCalls++ }
                                        if ($button -ceq 'right' -and $count -eq 1) { $rightClickToolCalls++ }
                                    }
                                }
                                elseif ($tool['toolName'] -match '^(functions\.)?ui_screenshot$') {
                                    $captureArguments = ([string]$tool['content']) | ConvertFrom-Json -AsHashtable -Depth 100
                                    $freshCaptureAvailable = $tool['toolStatus'] -eq 'Completed' -and
                                        $captureArguments -is [Collections.IDictionary] -and $captureArguments['title'] -is [string] -and
                                        $captureArguments['title'].IndexOf($result.targetToken, [StringComparison]::OrdinalIgnoreCase) -ge 0
                                    if (-not $freshCaptureAvailable) {
                                        $screenshotAfterClick = $false
                                        continue
                                    }
                                    if ($clickToolCalls -gt 0) { $screenshotAfterClick = $true }
                                    else { $screenshotBeforeClick = $true }
                                }
                            }
                            $additionalAttemptCount = [Math]::Max(0, $clickToolCalls - $requiredClickActions)
                            $result.actual = [ordered]@{
                                confirmed = $state['confirmed']; clicks = $state['clicks']; outsideClicks = $state['outsideClicks']
                                visualCode = $visualCode; finalAssistantReply = $finalReply; clickToolCalls = $clickToolCalls
                                clickAttemptLimit = $clickAttemptLimit; additionalAttemptCount = $additionalAttemptCount
                                usedAdditionalAttempt = $additionalAttemptCount -gt 0; uniqueCaptureIdCount = $captureIds.Count
                                clickAttemptsWithinBudget = $clickToolCalls -ge $requiredClickActions -and $clickToolCalls -le $clickAttemptLimit
                                freshCaptureBeforeEachAttempt = $freshCaptureBeforeEachAttempt
                                captureIdsUniqueAndNonempty = $captureIdsUniqueAndNonempty
                                screenshotBeforeClick = $screenshotBeforeClick; screenshotAfterClick = $screenshotAfterClick
                            }
                            if ($result.scenario -eq 'vision-gestures') {
                                $result.actual['gesturesEnabled'] = $state['gesturesEnabled']
                                $result.actual['doubleClicks'] = $state['doubleClicks']
                                $result.actual['rightClicks'] = $state['rightClicks']
                                $result.actual['doubleClickToolCalls'] = $doubleClickToolCalls
                                $result.actual['rightClickToolCalls'] = $rightClickToolCalls
                                $result.actual['screenshotBetweenClicks'] = $screenshotBetweenClicks
                            }
                            else {
                                $result.actual['finalReplyContainsCode'] = $visualCode -cmatch '^[0-9A-F]{7}$' -and
                                    $finalReply -cmatch ('(?<![A-Za-z0-9])' + [regex]::Escape($visualCode) + '(?![A-Za-z0-9])')
                            }
                        }
                        else { $result.actual = $state[$(if ($stateKeys.ContainsKey($result.scenario)) { $stateKeys[$result.scenario] } else { $result.scenario })] }
                        $result.actual['targetOpen'] = $state['ready'] -eq $true -and
                            $state['closed'] -eq $false -and -not $fixtureProcess.HasExited
                        if ($result.scenario -eq 'grid') {
                            $finalReply = if ($assistantMessages.Count -gt 0) { [string]$assistantMessages[-1]['content'] } else { '' }
                            $result.actual['finalAssistantReply'] = $finalReply
                            $result.actual['replyHasCustomer'] = $finalReply.Contains('Contoso Pharmacy', [StringComparison]::Ordinal)
                            $result.actual['replyHasAmount'] = $finalReply -match '(?<![0-9])2,?318\.40?(?![0-9])'
                        }
                        if ($result.scenario -eq 'document') {
                            $actual = $result.actual
                            $file = [IO.Path]::GetFullPath((Join-Path $result.directory 'automation-note.txt'))
                            $actual['expectedFilePath'] = $file
                            $actual['filePathMatches'] = -not [string]::IsNullOrWhiteSpace($actual['filePath']) -and
                                [string]::Equals([IO.Path]::GetFullPath([string]$actual['filePath']), $file, [StringComparison]::OrdinalIgnoreCase)
                            $actual['fileExists'] = [IO.File]::Exists($file)
                            $actual['utf8Valid'] = $false
                            $actual['normalizedText'] = $null
                            $actual['byteLength'] = $null
                            $actual['sha256'] = $null
                            $actual['hasUtf8Bom'] = $false
                            $actual['rawUtf8Base64'] = $null
                            if ($actual['fileExists']) {
                                $bytes = [IO.File]::ReadAllBytes($file)
                                $actual['byteLength'] = $bytes.Length
                                $actual['sha256'] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
                                $actual['rawUtf8Base64'] = [Convert]::ToBase64String($bytes)
                                $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
                                $actual['utf8Valid'] = $true
                                $actual['hasUtf8Bom'] = $text.StartsWith([string][char]0xFEFF, [StringComparison]::Ordinal)
                                if ($actual['hasUtf8Bom']) { $text = $text.Substring(1) }
                                $actual['normalizedText'] = $text.Replace("`r`n", "`n").Replace("`r", "`n")
                            }
                        }
                        elseif ($result.scenario -eq 'scroll') {
                            $actual = $result.actual
                            $actual['scrollPositionVerified'] = $actual['topIndex'] -gt $actual['initialTopIndex'] -and
                                $actual['confirmedTopIndex'] -gt $actual['initialTopIndex'] -and
                                $actual['maxTopIndex'] -ge $actual['confirmedTopIndex'] -and
                                $actual['lastRowTop'] -ge 0 -and $actual['lastRowBottom'] -gt $actual['lastRowTop'] -and
                                $actual['lastRowBottom'] -le $actual['viewportHeight'] -and $actual['viewportHeight'] -gt 0
                        }
                    }
                    $result.checks = @(Get-FieldChecks $result.expected $result.actual)
                    $result.assertionPassed = @($result.checks | Where-Object { -not $_.passed }).Count -eq 0
                }
                catch { $result.errors.Add((Protect-Text "Independent verification failed: $($_.Exception.Message)")) }
                $result.errorFlags.assertionFailed = -not $result.assertionPassed
            }
            if ($null -ne $fixtureProcess) {
                try {
                    if (-not $fixtureProcess.HasExited) {
                        if ($sendAttempted -and -not $sessionQuiescent) {
                            $result.fixtureRetainedForActiveChat = $true
                            $message = "Fixture PID $($fixtureProcess.Id) retained: stop chat $($result.chatId) before closing its target window."
                            $result.errors.Add($message)
                            Write-Host $message
                        }
                        else {
                            $current = [Diagnostics.Process]::GetProcessById($fixtureProcess.Id)
                            try {
                                if ($current.StartTime.ToFileTimeUtc() -ne $fixtureStartTicks) {
                                    throw 'Fixture PID identity changed; refusing to stop it.'
                                }
                                Stop-Process -Id $fixtureProcess.Id -ErrorAction Stop
                                if (-not $fixtureProcess.WaitForExit(5000)) { throw 'Owned fixture did not exit after cleanup.' }
                            }
                            finally { $current.Dispose() }
                        }
                    }
                    $result.fixtureCleanedUp = $fixtureProcess.HasExited
                }
                catch {
                    $result.errorFlags.cleanupError = $true
                    $result.errors.Add((Protect-Text $_.Exception.Message))
                    $bundle.abortedReason = 'Owned fixture cleanup could not be confirmed.'
                }
                finally { $fixtureProcess.Dispose() }
            }
            if ($result.scenario -eq 'calculator' -and $null -ne $result.target -and $result.target.ready) {
                if ($sendAttempted -and -not $sessionQuiescent) {
                    $result.errors.Add("Calculator window $($result.targetToken) retained: stop chat $($result.chatId) first.")
                }
                else {
                    # Close only the launched window, and only while it still is that Calculator window.
                    $handle = [long]$result.target.windowHandle
                    if (@([LumiAutomationBench.NativeWindows]::Snapshot() | Where-Object {
                            $_.Handle -eq $handle -and $_.Title -ceq 'Calculator' }).Count -eq 1) {
                        $result.target.closeRequested = [LumiAutomationBench.NativeWindows]::PostMessage(
                            [IntPtr]$handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
                    }
                }
            }
            if ($result.status -ne 'skipped') {
                $result.success = $sendAttempted -and $result.idleObserved -and $result.assertionPassed -and
                    $result.transcriptComplete -and -not ($result.errorFlags.Values -contains $true)
                $result.status = if ($result.success) { 'passed' } else { 'failed' }
            }
            Write-ArtifactJson (Join-Path $result.directory 'result.json') $result
            Save-Results
            Write-Host "$($result.caseId): $($result.status); elapsedMs=$($result.elapsedMs); tools=$($result.toolCallCount)"
            if ($result.scenario -eq 'notepad' -and $result.target -is [Collections.IDictionary] -and $result.target['launchProcessId']) {
                Write-Host "Notepad retained: launch PID $($result.target['launchProcessId']), window PID $($result.target['windowProcessId'])."
            }
        }
    }
}
catch {
    $bundle.abortedReason = Protect-Text $_.Exception.Message
    throw (Protect-Text $_.Exception.Message)
}
finally {
    try {
        $bundle.finishedAt = [DateTimeOffset]::UtcNow.ToString('o')
        Save-Results
    }
    finally {
        if ($null -ne $script:http) { $script:http.Dispose() }
        if ($hasMutex) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
        $script:bridgeToken = ''
        $script:bridge = $null
    }
}
Write-Host "Authoritative results: $(Join-Path $runRoot 'results.json')"
[pscustomobject]$bundle.summary
if ($bundle.summary.passed -ne $bundle.summary.planned) {
    throw 'Benchmark suite is not all-pass. Failed, skipped, and unattempted cases remain in results.json.'
}
