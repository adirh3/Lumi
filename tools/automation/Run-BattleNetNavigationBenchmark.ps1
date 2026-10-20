#requires -Version 7.2
<#
.SYNOPSIS
Measures safe game-page navigation through fresh Debug Lumi chats.
.DESCRIPTION
Requires Battle.net already running and an isolated Debug Lumi PID. Native
default-action setup preserves foreground focus and needs no physical input. Only changes
the selected game page between Diablo IV and Heroes of the Storm; never clicks
Install, Play, Update, Pause or Uninstall. Leaves Heroes selected. The exact same
prompt, model and effort should be used for both builds. App startup is excluded;
fresh-chat session/model latency is included. All results, including failures, are
preserved locally. Do not run alongside another desktop automation task.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$LumiProcessId,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$Label,
    [ValidateRange(1, 5)][int]$Iterations = 2,
    [string]$Model = 'gpt-6-sol',
    [string]$ReasoningEffort = 'low',
    [string]$BinaryDirectory = (Join-Path $PSScriptRoot '..\..\.mcp-run\uia-validation\bin\Lumi\debug')
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This benchmark requires Windows.' }
$discoveryRoot = Join-Path $env:LOCALAPPDATA 'Lumi\debug-bridges'
$targetProcess = [Diagnostics.Process]::GetProcessById($LumiProcessId)
try { $processStartedAt = $targetProcess.StartTime.ToUniversalTime() }
finally { $targetProcess.Dispose() }
$bridge = @(Get-ChildItem $discoveryRoot -Filter '*.json' | ForEach-Object {
    Get-Content $_.FullName -Raw | ConvertFrom-Json
} | Where-Object {
    $_.processId -eq $LumiProcessId -and
    ([DateTimeOffset]$_.startedAt).UtcDateTime -ge $processStartedAt.AddSeconds(-2)
} | Sort-Object { [DateTimeOffset]$_.startedAt } -Descending | Select-Object -First 1)
if ($bridge.Count -ne 1) { throw 'Exactly one live Debug bridge must match the supplied PID.' }
$bridge = $bridge[0]
$uri = [uri]$bridge.url
if ($uri.Scheme -ne 'http' -or $uri.Host -ne '127.0.0.1') { throw 'Only a local Debug bridge is permitted.' }
$headers = @{ 'X-Lumi-Debug-Token' = $bridge.token }
$runDirectory = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) "$Label-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))"
[void][IO.Directory]::CreateDirectory($runDirectory)
$results = [Collections.Generic.List[object]]::new()

function Invoke-Bridge([string]$Action, [hashtable]$Arguments = @{}) {
    $body = @{ action = $Action; arguments = $Arguments } | ConvertTo-Json -Depth 15 -Compress
    $response = Invoke-RestMethod -Uri "$($bridge.url)/invoke" -Headers $headers -Method Post `
        -ContentType 'application/json' -Body $body -TimeoutSec 210 -NoProxy
    if (-not $response.ok) { throw $response.error }
    return $response.result
}

function Save-Results {
    $bundle = [ordered]@{
        label = $Label; model = $Model; reasoningEffort = $ReasoningEffort
        prompt = $prompt; lumiProcessId = $LumiProcessId; iterations = $Iterations
        results = @($results.ToArray())
    }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'results.json'),
        ($bundle | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
}

[void][Reflection.Assembly]::LoadFrom((Join-Path $BinaryDirectory 'FlaUI.Core.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $BinaryDirectory 'FlaUI.UIA3.dll'))
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class BattleNetBenchmarkWindow {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@
$processes = @(Get-Process -Name 'Battle.net' | Where-Object MainWindowHandle -ne 0)
if ($processes.Count -ne 1) { throw 'Exactly one Battle.net window must already be open.' }
$window = $processes[0].MainWindowHandle
$processes[0].Dispose()
$automation = [FlaUI.UIA3.UIA3Automation]::new()
$root = $automation.FromHandle($window)

function Primary-Action([string]$Game) {
    $flags = [FlaUI.Core.Definitions.PropertyConditionFlags]::MatchSubstring
    foreach ($prefix in 'Play: ', 'Install: ', 'Update: ') {
        $condition = [FlaUI.Core.Conditions.AndCondition]::new(
            $automation.ConditionFactory.ByControlType([FlaUI.Core.Definitions.ControlType]::Button),
            $automation.ConditionFactory.ByName($prefix + $Game, $flags))
        $button = $root.FindFirstDescendant($condition)
        if ($null -ne $button) { return $button.Name }
    }
    return $null
}

function Navigate-Game([string]$Id, [string]$Game) {
    $foreground = [BattleNetBenchmarkWindow]::GetForegroundWindow()
    $tab = $root.FindFirstDescendant($automation.ConditionFactory.ByAutomationId($Id))
    if ($null -eq $tab -or $tab.ControlType -ne [FlaUI.Core.Definitions.ControlType]::TabItem) {
        throw 'The expected game navigation tab is unavailable.'
    }
    if (-not $tab.Patterns.LegacyIAccessible.IsSupported -or
        [string]::IsNullOrWhiteSpace($tab.Patterns.LegacyIAccessible.Pattern.DefaultAction.ValueOrDefault)) {
        throw 'The game tab has no background activation action; benchmark setup will not use physical input.'
    }
    $tab.Patterns.LegacyIAccessible.Pattern.DoDefaultAction()
    $wait = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (Primary-Action $Game) {
            if ([BattleNetBenchmarkWindow]::GetForegroundWindow() -ne $foreground) {
                throw 'Battle.net setup changed foreground focus unexpectedly.'
            }
            return
        }
        Start-Sleep -Milliseconds 50
    } while ($wait.ElapsedMilliseconds -lt 5000)
    throw "The actual content did not navigate to $Game."
}

$prompt = 'Using only desktop UI automation tools, open the Heroes of the Storm page in the already-running Battle.net client and report the exact label of its primary action button. Verify the game content, not only the navigation highlight. Do not press Install, Play, Update, Pause, Uninstall or any purchase button. Do not launch a game, run commands, change settings, or alter downloads. Give a brief answer when verified.'
$mutex = [Threading.Mutex]::new($false, 'Local\Lumi.UIAutomationBenchmarks.Desktop')
$locked = $false
$navigationStarted = $false
$modelIdle = $true
try {
    $locked = $mutex.WaitOne(0)
    if (-not $locked) { throw 'Another desktop benchmark is running.' }
    for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
        $status = Invoke-Bridge 'status'
        if ($status.app.isBusy -or $status.app.isStreaming -or $status.app.isSessionActive) {
            throw 'The target Debug Lumi has active work; no existing chat will be interrupted.'
        }
        $navigationStarted = $true
        Navigate-Game 'game-nav-btn-Fen' 'Diablo IV'
        $chat = Invoke-Bridge 'create_chat' @{
            title = "Battle.net navigation $Label $iteration"; model = $Model
            reasoningEffort = $ReasoningEffort; open = $true
        }
        $result = [ordered]@{
            iteration = $iteration; chatId = $chat.chat.id; passed = $false
            elapsedMs = $null; toolCalls = $null; toolDurationMs = $null
            firstToolLatencyMs = $null; primaryAction = $null; assistant = $null; error = $null
        }
        $results.Add($result)
        Save-Results
        $clock = [Diagnostics.Stopwatch]::StartNew()
        try {
            $modelIdle = $false
            [void](Invoke-Bridge 'send_message' @{ chatId = $chat.chat.id; message = $prompt; waitForIdle = $false })
            [void](Invoke-Bridge 'wait_for_idle' @{ chatId = $chat.chat.id; timeoutMs = 180000; pollIntervalMs = 100 })
            $modelIdle = $true
            $result.elapsedMs = [Math]::Round($clock.Elapsed.TotalMilliseconds, 2)
            $transcript = Invoke-Bridge 'read_transcript' @{ chatId = $chat.chat.id; maxMessages = 200; maxContentChars = 20000 }
            $file = Join-Path $bridge.appDataDir "chats\$($chat.chat.id).json"
            $messages = @(Get-Content -LiteralPath $file -Raw | ConvertFrom-Json)
            $persistenceWait = [Diagnostics.Stopwatch]::StartNew()
            while ($messages.Count -lt $transcript.totalMessages -and $persistenceWait.ElapsedMilliseconds -lt 2000) {
                Start-Sleep -Milliseconds 50
                $messages = @(Get-Content -LiteralPath $file -Raw | ConvertFrom-Json)
            }
            if ($messages.Count -lt $transcript.totalMessages) { throw 'The persisted transcript is incomplete.' }
            $tools = @($messages | Where-Object role -eq 'tool')
            $user = $messages | Where-Object role -eq 'user' | Select-Object -First 1
            $result.toolCalls = $tools.Count
            if (@($tools | Where-Object {$null -eq $_.toolDurationMs}).Count -eq 0) {
                $result.toolDurationMs = [Math]::Round(($tools | Measure-Object toolDurationMs -Sum).Sum, 2)
            }
            if ($tools.Count) {
                $result.firstToolLatencyMs = [Math]::Round(
                    ([DateTimeOffset]$tools[0].toolStartedAt - [DateTimeOffset]$user.timestamp).TotalMilliseconds, 2)
            }
            $result.primaryAction = Primary-Action 'Heroes of the Storm'
            $result.assistant = ($messages | Where-Object role -eq 'assistant' | Select-Object -Last 1).content
            $forbidden = @($tools | Where-Object {$_.toolName -notmatch '^(ui_|session_artifacts$)'})
            $result.passed = $null -ne $result.primaryAction -and $forbidden.Count -eq 0 `
                -and @($messages | Where-Object role -eq 'error').Count -eq 0 `
                -and $user.model -eq $Model
            [IO.File]::WriteAllText((Join-Path $runDirectory "transcript-$iteration.json"),
                ($messages | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
            Write-Host "$Label iteration ${iteration}: passed=$($result.passed), elapsedMs=$($result.elapsedMs), tools=$($result.toolCalls)"
        }
        catch {
            $result.error = $_.Exception.Message.Replace($bridge.token, '[REDACTED]')
            throw
        }
        finally { Save-Results }
    }
}
finally {
    try {
        if ($locked -and $navigationStarted -and $modelIdle) {
            Navigate-Game 'game-nav-btn-Hero' 'Heroes of the Storm'
        }
    }
    finally {
        $automation.Dispose()
        if ($locked) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}
Write-Host "Results: $(Join-Path $runDirectory 'results.json')"
if (@($results | Where-Object {-not $_.passed}).Count) { throw 'A navigation benchmark failed; see recorded results.' }
