#requires -Version 7.2
<#
.SYNOPSIS
One repository command for Lumi's Windows computer-use validation.
.DESCRIPTION
Runs the selected tiers and reports every test and benchmark scenario individually.
Skipped or unattempted items are never counted as passed.

  Contracts  Builds Lumi.Tests (Release) and runs the UI automation contract, prompt,
             tool-schema and Desktop preview tests; the headless Desktop preview UI
             tests run in their own test host.
  Native     Runs UIAutomationDesktopTests against disposable WinForms/WPF fixtures on
             this desktop. When no Notepad window is open, it also prepares a disposable
             Notepad file for the real-Notepad test and closes that window afterwards.
             Background tests also run in a disconnected/locked session; physical-input
             tests then skip with the reason.
  BattleNet  Opt-in: tests against a real, signed-in Battle.net app. -BattleNetNavigation
             also runs the navigation benchmark through the Model tier's Lumi instance.
  Model      Builds Debug Lumi into .mcp-run, launches it with an isolated, seeded app-data
             folder (never your real chats, memories or settings), runs every benchmark
             scenario with the chosen model through Run-UIAutomationBenchmarks.ps1, and
             closes that instance by PID. Uses the machine's existing Copilot sign-in.

Physical-input tests and scenarios need an unlocked, connected, idle desktop; they move
the pointer and send keys to disposable windows only. Leave the desktop alone while they run.
Artifacts: <OutputDirectory>\<run id>\summary.json plus per-tier logs and TRX files.
Exit code: 0 when every selected item passed, 1 when anything failed, 2 when nothing failed
but items were skipped or not run (0 with -AllowSkips).
.EXAMPLE
pwsh tools\automation\Invoke-UIAutomationValidation.ps1
.EXAMPLE
pwsh tools\automation\Invoke-UIAutomationValidation.ps1 -Tiers Contracts,Native
.EXAMPLE
pwsh tools\automation\Invoke-UIAutomationValidation.ps1 -Tiers Model -Scenarios tree,grid,wpf -Iterations 3
#>
[CmdletBinding()]
param(
    [string[]]$Tiers = @('Contracts', 'Native', 'Model'),
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'Lumi-UIAutomationValidation'),
    [string[]]$Scenarios,
    [ValidateRange(1, 100)][int]$Iterations = 1,
    [ValidateNotNullOrEmpty()][string]$Model = 'gpt-6-sol',
    [ValidateNotNullOrEmpty()][string]$ReasoningEffort = 'low',
    [ValidateRange(0, 2147483647)][int]$LumiProcessId = 0,
    [switch]$KeepLumiOpen,
    [switch]$BattleNetNavigation,
    [switch]$AllowSkips,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
if (-not $IsWindows) { throw 'Windows computer-use validation requires Windows.' }
# `pwsh -File` passes "A,B" as one string; accept both forms.
function Split-List { param([string[]]$Values) @($Values | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
$Tiers = Split-List $Tiers
$unknown = @($Tiers | Where-Object { $_ -notin @('Contracts', 'Native', 'BattleNet', 'Model') })
if ($unknown) { throw "Unknown tier(s): $($unknown -join ', '). Use Contracts, Native, BattleNet or Model." }
if ($Scenarios) { $Scenarios = Split-List $Scenarios }

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$runId = 'validation-{0}-{1}' -f [DateTime]::Now.ToString('yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N').Substring(0, 8)
$runRoot = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) $runId
[void][IO.Directory]::CreateDirectory($runRoot)
$testArtifacts = Join-Path $repo '.mcp-run\uia-validation'
$testProject = Join-Path $repo 'tests\Lumi.Tests\Lumi.Tests.csproj'
$items = [Collections.Generic.List[object]]::new()

if (-not ('LumiValidation.Desktop' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace LumiValidation {
    public static class Desktop {
        [DllImport("wtsapi32.dll")] static extern bool WTSQuerySessionInformation(IntPtr s, uint id, int c, out IntPtr b, out uint n);
        [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr m);
        [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint f, bool i, uint a);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr h, int i, StringBuilder s, int n, out uint needed);
        [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        public static string PhysicalInputUnavailableReason() {
            IntPtr buffer; uint bytes;
            if (WTSQuerySessionInformation(IntPtr.Zero, uint.MaxValue, 8, out buffer, out bytes)) {
                try { if (Marshal.ReadInt32(buffer) != 0) return "The Windows session is disconnected."; }
                finally { WTSFreeMemory(buffer); }
            }
            var desktop = OpenInputDesktop(0, false, 1);
            if (desktop == IntPtr.Zero) return "The PC is locked or showing a secure prompt.";
            try {
                var name = new StringBuilder(64); uint needed;
                if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out needed) ||
                    !string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase))
                    return "The PC is locked or showing a secure prompt.";
            }
            finally { CloseDesktop(desktop); }
            return GetForegroundWindow() == IntPtr.Zero ? "No interactive foreground window is available." : null;
        }
        public static string[] Windows() {
            var result = new List<string>();
            EnumWindows((h, l) => {
                if (!IsWindowVisible(h)) return true;
                var text = new StringBuilder(1024);
                if (GetWindowText(h, text, text.Capacity) == 0) return true;
                uint pid; GetWindowThreadProcessId(h, out pid);
                result.Add(h.ToInt64() + "|" + pid + "|" + text);
                return true;
            }, IntPtr.Zero);
            return result.ToArray();
        }
    }
}
'@
}

function Add-Item {
    param([string]$Tier, [string]$Name, [string]$Outcome, $DurationMs = $null, [string]$Detail = $null)
    $items.Add([pscustomobject][ordered]@{
        tier = $Tier; name = $Name; outcome = $Outcome
        durationMs = if ($null -ne $DurationMs) { [Math]::Round([double]$DurationMs) } else { $null }
        detail = if ($Detail -and $Detail.Length -gt 600) { $Detail.Substring(0, 600) + '...' } else { $Detail }
    })
}

function Invoke-Logged {
    param([string]$Label, [string]$Log, [string]$File, [string[]]$Arguments, [hashtable]$Environment = @{})
    Write-Host "[$Label] $File $($Arguments -join ' ')"
    $saved = @{}
    foreach ($key in $Environment.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $Environment[$key])
    }
    try { & $File @Arguments *>> $Log }
    finally { foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) } }
    return $LASTEXITCODE
}

function Get-NotepadWindows {
    @([LumiValidation.Desktop]::Windows() | ForEach-Object {
        $parts = $_.Split('|', 3)
        $process = Get-Process -Id ([int]$parts[1]) -ErrorAction SilentlyContinue
        if ($null -ne $process -and $process.ProcessName -ieq 'notepad') {
            [pscustomobject]@{ Handle = [long]$parts[0]; Title = $parts[2] }
        }
    })
}

function Invoke-TestTier {
    param([string]$Tier, [string[]]$Classes, [hashtable]$Environment = @{})
    $name = ($Tier + '-' + ($Classes[0] -replace '^.*\.', '')).ToLowerInvariant()
    $results = Join-Path $runRoot $name
    $filter = ($Classes | ForEach-Object { "FullyQualifiedName~$_." }) -join '|'
    $exit = Invoke-Logged $Tier (Join-Path $runRoot "$name.log") 'dotnet' @(
        'test', $testProject, '-c', 'Release', '--no-build', '--nologo', '--artifacts-path', $testArtifacts,
        '--filter', $filter, '--logger', 'trx;LogFileName=results.trx', '--results-directory', $results,
        '--blame-hang', '--blame-hang-timeout', '5m', '--blame-hang-dump-type', 'none') $Environment
    $trx = Get-ChildItem $results -Recurse -Filter results.trx -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $trx) {
        Add-Item $Tier ($Classes -join ', ') 'failed' $null "No test results were produced (exit $exit); see $name.log."
        return
    }
    [xml]$document = Get-Content -LiteralPath $trx.FullName -Raw
    $manager = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $manager.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $count = 0
    foreach ($node in $document.SelectNodes('//t:UnitTestResult', $manager)) {
        $count++
        $outcome = switch ($node.GetAttribute('outcome')) { 'Passed' { 'passed' } 'NotExecuted' { 'skipped' } default { 'failed' } }
        # Failure messages and xUnit skip reasons are both reported as the result's error message.
        $message = $node.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $manager)
        $detail = if ($outcome -ne 'passed' -and $null -ne $message) { ($message.InnerText -replace '\s+', ' ').Trim() } else { $null }
        $duration = if ($node.HasAttribute('duration')) { [TimeSpan]::Parse($node.GetAttribute('duration')).TotalMilliseconds } else { $null }
        Add-Item $Tier ($node.GetAttribute('testName') -replace '^Lumi\.Tests\.', '') $outcome $duration $detail
    }
    if ($count -eq 0 -or ($exit -ne 0 -and -not @($items | Where-Object { $_.tier -eq $Tier -and $_.outcome -eq 'failed' }))) {
        Add-Item $Tier "$name host" 'failed' $null "The test host exited with $exit; see $name.log."
    }
}

function Start-IsolatedLumi {
    $app = Join-Path $repo '.mcp-run\uia-bench-app'
    if (-not $NoBuild) {
        $exit = Invoke-Logged 'Model' (Join-Path $runRoot 'build-lumi.log') 'dotnet' @(
            'build', (Join-Path $repo 'src\Lumi\Lumi.csproj'), '-c', 'Debug', '-o', $app, '--nologo', '-v', 'q')
        if ($exit -ne 0) { throw "Debug Lumi build failed; see $(Join-Path $runRoot 'build-lumi.log')." }
    }
    $exe = Join-Path $app 'Lumi.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "No Debug Lumi build at $exe; run without -NoBuild." }
    $dataRoot = Join-Path $runRoot 'lumi-appdata'
    [void][IO.Directory]::CreateDirectory((Join-Path $dataRoot 'Lumi'))
    # A clean profile: no memories, skills or chats of the user; no background title/memory model calls.
    $seed = [ordered]@{ settings = [ordered]@{
        isOnboarded = $true; userName = 'Validation'; autoGenerateTitles = $false
        enableMemoryAutoSave = $false; enableMemoryAutoMaintenance = $false
        preferredModel = $Model; reasoningEffort = $ReasoningEffort
    } }
    [IO.File]::WriteAllText((Join-Path $dataRoot 'Lumi\data.json'), (ConvertTo-Json $seed -Depth 5), [Text.UTF8Encoding]::new($false))
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $app
    $start.Environment['LUMI_APPDATA_DIR'] = $dataRoot
    $start.ArgumentList.Add('--skip-onboarding')
    $process = [Diagnostics.Process]::Start($start)
    $bridges = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Lumi\debug-bridges'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 120 -and -not $process.HasExited) {
        $ready = Get-ChildItem $bridges -Filter *.json -ErrorAction SilentlyContinue |
            Where-Object LastWriteTime -ge $process.StartTime.AddSeconds(-2) | ForEach-Object {
                try { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json } catch { }
            } | Where-Object { $_.processId -eq $process.Id -and $_.appDataDir -like "$dataRoot*" }
        if ($ready) {
            Write-Host "[Model] Isolated Debug Lumi PID $($process.Id) is ready (app data: $dataRoot)."
            return [pscustomobject]@{ Id = $process.Id; Path = $exe; Owned = $true }
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
    throw 'The isolated Debug Lumi did not publish its debug bridge within 120 seconds.'
}

function Stop-OwnedLumi {
    param($Lumi)
    if ($null -eq $Lumi -or -not $Lumi.Owned) { return }
    $process = Get-Process -Id $Lumi.Id -ErrorAction SilentlyContinue
    # Close only the instance this command launched, identified by PID and executable path.
    if ($null -ne $process -and [string]::Equals($process.Path, $Lumi.Path, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Process -Id $Lumi.Id
        [void]$process.WaitForExit(10000)
        Write-Host "[Model] Closed isolated Debug Lumi PID $($Lumi.Id)."
    }
}

$unavailable = [LumiValidation.Desktop]::PhysicalInputUnavailableReason()
$summary = [ordered]@{
    runId = $runId; startedAt = [DateTimeOffset]::Now.ToString('o'); finishedAt = $null; tiers = $Tiers
    model = $Model; reasoningEffort = $ReasoningEffort
    environment = [ordered]@{
        os = [Environment]::OSVersion.VersionString
        physicalInputAvailable = $null -eq $unavailable; physicalInputUnavailableReason = $unavailable
    }
    counts = $null; verdict = $null; items = $items
}
Write-Host "Validation run $runId -> $runRoot"
if ($unavailable) { Write-Warning "Physical input is unavailable: $unavailable Physical-input tests and scenarios will skip." }

try {
    if (-not $NoBuild -and ($Tiers | Where-Object { $_ -in @('Contracts', 'Native', 'BattleNet') })) {
        $exit = Invoke-Logged 'Build' (Join-Path $runRoot 'build-tests.log') 'dotnet' @(
            'build', $testProject, '-c', 'Release', '--artifacts-path', $testArtifacts, '--nologo', '-v', 'q')
        if ($exit -ne 0) { throw "Test build failed; see $(Join-Path $runRoot 'build-tests.log')." }
    }

    if ('Contracts' -in $Tiers) {
        Invoke-TestTier 'Contracts' @('Lumi.Tests.UIAutomationTests', 'Lumi.Tests.UIAutomationCaptureTests',
            'Lumi.Tests.DesktopPreviewTests', 'Lumi.Tests.SystemPromptBuilderTests', 'Lumi.Tests.ToolDisplayHelperTests',
            'Lumi.Tests.AgentsViewModelToolSelectionTests', 'Lumi.Tests.ManagementToolSerializerOptionsTests',
            'Lumi.Tests.ChatWorkspaceViewTests')
        # Headless UI tests need their own test host.
        Invoke-TestTier 'Contracts' @('Lumi.Tests.DesktopPreviewUiTests')
    }

    if ('Native' -in $Tiers) {
        $environment = @{ LUMI_UI_AUTOMATION_DESKTOP_TESTS = '1' }
        $notepad = $null
        $visibleNotepads = @(Get-NotepadWindows)
        # Proceed only if every visible Notepad window was left by these tools; the file then opens as a
        # tab in one of them or in a new window, found by the unique token in its title. A user's Notepad
        # window means skip, since the file could open as a tab there.
        $foreign = @($visibleNotepads | Where-Object { $_.Title -notmatch 'Lumi-UI(-Bench|A-Native)-[0-9a-f]{32}' })
        if ($null -eq $unavailable -and $foreign.Count -eq 0) {
            # A disposable file in a new Notepad window; closed afterwards only if it saved cleanly.
            $token = 'Lumi-UIA-Native-' + [guid]::NewGuid().ToString('N')
            $file = Join-Path $runRoot "$token.txt"
            [IO.File]::WriteAllText($file, 'Disposable native test document.', [Text.UTF8Encoding]::new($false))
            Start-Process -FilePath (Join-Path $env:WINDIR 'System32\notepad.exe') -ArgumentList "`"$file`""
            $watch = [Diagnostics.Stopwatch]::StartNew()
            while ($null -eq $notepad -and $watch.Elapsed.TotalSeconds -lt 15) {
                $notepad = Get-NotepadWindows | Where-Object { $_.Title.Contains($token) } | Select-Object -First 1
                if ($null -eq $notepad) { Start-Sleep -Milliseconds 200 }
            }
            if ($null -ne $notepad) {
                $environment['LUMI_UI_AUTOMATION_NOTEPAD_WINDOW'] = [string]$notepad.Handle
                $environment['LUMI_UI_AUTOMATION_NOTEPAD_FILE'] = $file
            }
        }
        try { Invoke-TestTier 'Native' @('Lumi.Tests.UIAutomationDesktopTests') $environment }
        finally {
            # Close only a window this command created, and only if its document saved cleanly.
            if ($null -ne $notepad -and $notepad.Handle -notin @($visibleNotepads | ForEach-Object Handle)) {
                $current = Get-NotepadWindows | Where-Object Handle -eq $notepad.Handle
                if ($null -ne $current -and -not $current.Title.StartsWith('*')) {
                    [void][LumiValidation.Desktop]::PostMessage([IntPtr]$notepad.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
                }
                elseif ($null -ne $current) { Write-Warning "Left the test Notepad window open because it has unsaved changes: $($current.Title)" }
            }
        }
    }

    if ('BattleNet' -in $Tiers) {
        Invoke-TestTier 'BattleNet' @('Lumi.Tests.BattleNetAutomationTests') @{ LUMI_BATTLENET_UI_TESTS = '1' }
    }

    if ('Model' -in $Tiers -or $BattleNetNavigation) {
        $lumi = if ($LumiProcessId -ne 0) { [pscustomobject]@{ Id = $LumiProcessId; Path = $null; Owned = $false } } else { Start-IsolatedLumi }
        try {
            if ('Model' -in $Tiers) {
                $modelRoot = Join-Path $runRoot 'model'
                $benchmark = @{
                    LumiProcessId = $lumi.Id; OutputDirectory = $modelRoot; Label = 'validation'
                    Iterations = $Iterations; Model = $Model; ReasoningEffort = $ReasoningEffort
                }
                if ($Scenarios) { $benchmark.Scenarios = $Scenarios }
                # The runner prints per-case progress and throws when not everything passed;
                # results.json is authoritative either way.
                try { & (Join-Path $PSScriptRoot 'Run-UIAutomationBenchmarks.ps1') @benchmark }
                catch { Write-Warning "Benchmark runner: $($_.Exception.Message)" }
                $resultsFile = Get-ChildItem $modelRoot -Recurse -Filter results.json -ErrorAction SilentlyContinue |
                    Where-Object { $_.Directory.Parent.FullName -eq $modelRoot } | Select-Object -First 1
                if ($null -eq $resultsFile) { Add-Item 'Model' 'benchmark runner' 'failed' $null 'The benchmark runner produced no results.json.' }
                else {
                    $bundle = Get-Content -LiteralPath $resultsFile.FullName -Raw | ConvertFrom-Json
                    foreach ($case in $bundle.results) {
                        $outcome = switch ($case.status) { 'passed' { 'passed' } 'skipped' { 'skipped' } 'not-run' { 'not-run' } default { 'failed' } }
                        $failedChecks = @($case.checks | Where-Object { -not $_.passed } | ForEach-Object { "$($_.field): expected $($_.expected), actual $($_.actual)" })
                        $detail = @($case.errors) + @($case.policyViolations) + $failedChecks | Where-Object { $_ }
                        Add-Item 'Model' "$($case.caseId) ($($case.toolCallCount) tools)" $outcome $case.elapsedMs ($detail -join ' | ')
                    }
                }
            }
            if ($BattleNetNavigation) {
                $exit = Invoke-Logged 'BattleNet' (Join-Path $runRoot 'battlenet-navigation.log') 'pwsh' @('-NoProfile', '-File',
                    (Join-Path $PSScriptRoot 'Run-BattleNetNavigationBenchmark.ps1'), '-LumiProcessId', $lumi.Id,
                    '-OutputDirectory', (Join-Path $runRoot 'battlenet'), '-Label', 'validation',
                    '-Model', $Model, '-ReasoningEffort', $ReasoningEffort,
                    '-BinaryDirectory', (Join-Path $testArtifacts 'bin\Lumi.Tests\release'))
                Add-Item 'BattleNet' 'navigation benchmark' $(if ($exit -eq 0) { 'passed' } else { 'failed' }) $null 'See battlenet-navigation.log.'
            }
        }
        finally { if (-not $KeepLumiOpen) { Stop-OwnedLumi $lumi } }
    }
}
catch {
    Add-Item 'Runner' 'validation command' 'failed' $null $_.Exception.Message
}
finally {
    $summary.finishedAt = [DateTimeOffset]::Now.ToString('o')
    $summary.counts = [ordered]@{
        total = $items.Count
        passed = @($items | Where-Object outcome -eq 'passed').Count
        failed = @($items | Where-Object outcome -eq 'failed').Count
        skipped = @($items | Where-Object outcome -eq 'skipped').Count
        notRun = @($items | Where-Object outcome -eq 'not-run').Count
    }
    $summary.verdict = if ($summary.counts.failed -gt 0) { 'failed' }
        elseif ($summary.counts.skipped + $summary.counts.notRun -gt 0) { 'incomplete' }
        elseif ($summary.counts.total -eq 0) { 'empty' }
        else { 'passed' }
    [IO.File]::WriteAllText((Join-Path $runRoot 'summary.json'), (ConvertTo-Json $summary -Depth 6), [Text.UTF8Encoding]::new($false))
}

$items | Where-Object outcome -ne 'passed' | Format-Table tier, outcome, name, detail -Wrap -AutoSize | Out-String -Width 220 | Write-Host
$items | Group-Object tier | ForEach-Object {
    $group = $_.Group
    Write-Host ('{0,-10} passed {1,3} | failed {2,3} | skipped {3,3} | not run {4,3}' -f $_.Name,
        @($group | Where-Object outcome -eq 'passed').Count, @($group | Where-Object outcome -eq 'failed').Count,
        @($group | Where-Object outcome -eq 'skipped').Count, @($group | Where-Object outcome -eq 'not-run').Count)
}
Write-Host "Verdict: $($summary.verdict). Summary: $(Join-Path $runRoot 'summary.json')"
exit $(switch ($summary.verdict) { 'passed' { 0 } 'incomplete' { if ($AllowSkips) { 0 } else { 2 } } default { 1 } })
